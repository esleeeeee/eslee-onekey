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

        if (!string.IsNullOrWhiteSpace(_settings.GameProcessName))
        {
            await _processMonitor.StartAsync(
                _settings.GameProcessName,
                _settings.ProcessPollInterval,
                cancellationToken);
        }

        await _engine.RecoverStaleSessionAsync(cancellationToken);
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

        await _engine.OnGameExitedAsync(LifetimeToken);
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
