namespace Eslee.OneKey.Core;

public sealed class AutomationCoordinator : IAsyncDisposable
{
    private readonly AutomationSettings _settings;
    private readonly AutomationEngine _engine;
    private readonly IHotkeyService _hotkey;
    private readonly IProcessMonitor _processMonitor;
    private CancellationTokenSource? _lifetime;
    private Task? _restoreTask;

    public AutomationCoordinator(
        AutomationSettings settings,
        AutomationEngine engine,
        IHotkeyService hotkey,
        IProcessMonitor processMonitor)
    {
        _settings = settings;
        _engine = engine;
        _hotkey = hotkey;
        _processMonitor = processMonitor;
    }

    public bool IsPaused { get; private set; }
    public string? LastError { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _hotkey.Pressed += HandleHotkeyAsync;
        _processMonitor.ProcessStarted += HandleProcessStartedAsync;
        _processMonitor.ProcessExited += HandleProcessExitedAsync;

        var registration = await _hotkey.RegisterAsync(_settings.Hotkey, cancellationToken);
        if (!registration.Succeeded)
        {
            LastError = registration.Error ?? "전역 단축키 등록에 실패했습니다.";
        }

        // 감시 프로세스가 이미 실행 중이면 감시 시작이 곧바로 자동화를 트리거하므로,
        // 이전 세션 복구를 먼저 끝내 진행 중인 실행과 뒤엉키지 않게 한다.
        await _engine.RecoverStaleSessionAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(_settings.WatchProcessName))
        {
            await _processMonitor.StartAsync(
                _settings.WatchProcessName,
                _settings.ProcessPollInterval,
                cancellationToken);
        }
    }

    public void SetPaused(bool paused) => IsPaused = paused;

    public async Task HandleHotkeyAsync()
    {
        if (!IsPaused)
        {
            await _engine.StartAsync(AutomationTrigger.Hotkey, LifetimeToken);
        }
    }

    public async Task HandleProcessStartedAsync()
    {
        if (!IsPaused)
        {
            await _engine.StartAsync(AutomationTrigger.ProcessStarted, LifetimeToken);
        }
    }

    public async Task HandleProcessExitedAsync()
    {
        if (IsPaused)
        {
            return;
        }

        await _engine.OnWatchedProcessExitedAsync(LifetimeToken);
        StartRestorePollingIfNeeded();
    }

    public void StartRestorePollingIfNeeded()
    {
        if (!_engine.RestorePending || _restoreTask is { IsCompleted: false })
        {
            return;
        }
        _restoreTask = _engine.WaitForRestoreAsync(LifetimeToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync();
        }
        _hotkey.Pressed -= HandleHotkeyAsync;
        _processMonitor.ProcessStarted -= HandleProcessStartedAsync;
        _processMonitor.ProcessExited -= HandleProcessExitedAsync;
        _hotkey.Unregister();
        await _processMonitor.StopAsync();
        if (_restoreTask is not null)
        {
            try
            {
                await _restoreTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during application shutdown.
            }
        }
        await _processMonitor.DisposeAsync();
        await _hotkey.DisposeAsync();
        _lifetime?.Dispose();
    }

    private CancellationToken LifetimeToken => _lifetime?.Token ?? CancellationToken.None;
}
