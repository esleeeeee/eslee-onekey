namespace Eslee.OneKey.Core;

public sealed class AutomationCoordinator : IAsyncDisposable
{
    private readonly AutomationSettings _settings;
    private readonly AutomationEngine _engine;
    private readonly IHotkeyService _hotkey;
    private readonly IProcessMonitor _processMonitor;
    private readonly IReadOnlyList<AccountHotkey> _accountHotkeys;
    private CancellationTokenSource? _lifetime;
    private Task? _restoreTask;

    public AutomationCoordinator(
        AutomationSettings settings,
        AutomationEngine engine,
        IHotkeyService hotkey,
        IProcessMonitor processMonitor,
        IReadOnlyList<AccountHotkey>? accountHotkeys = null)
    {
        _settings = settings;
        _engine = engine;
        _hotkey = hotkey;
        _processMonitor = processMonitor;
        _accountHotkeys = accountHotkeys ?? [];
    }

    /// <summary>
    /// 계정별 단축키입니다. 같은 자동화를 시작하되 어떤 게임 계정으로 시작할지가
    /// 다릅니다. 프로필을 늘리면 단축키도 그만큼 늘어납니다.
    /// </summary>
    public sealed record AccountHotkey(GameAccountProfile Profile, IHotkeyService Hotkey);

    public bool IsPaused { get; private set; }
    public string? LastError { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _hotkey.Pressed += HandleHotkeyAsync;
        _processMonitor.ProcessStarted += HandleProcessStartedAsync;
        _processMonitor.ProcessExited += HandleProcessExitedAsync;

        // 계정 프로필이 같은 조합을 쓰면 그쪽이 더 구체적이므로 기본 단축키는 등록하지
        // 않는다. 같은 조합을 두 번 등록하면 Windows가 충돌로 거부한다.
        var claimedByAccount = _accountHotkeys.Any(account => account.Profile.Hotkey == _settings.Hotkey);
        if (!claimedByAccount)
        {
            var registration = await _hotkey.RegisterAsync(_settings.Hotkey, cancellationToken);
            if (!registration.Succeeded)
            {
                LastError = registration.Error ?? "전역 단축키 등록에 실패했습니다.";
            }
        }

        foreach (var account in _accountHotkeys)
        {
            var profile = account.Profile;
            account.Hotkey.Pressed += () => HandleAccountHotkeyAsync(profile);
            var accountRegistration = await account.Hotkey.RegisterAsync(
                account.Profile.Hotkey,
                cancellationToken);
            if (!accountRegistration.Succeeded)
            {
                LastError = accountRegistration.Error
                    ?? $"{account.Profile.Name} 단축키 등록에 실패했습니다.";
            }
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

    /// <summary>지정한 계정으로 자동화를 시작합니다.</summary>
    public async Task HandleAccountHotkeyAsync(GameAccountProfile profile)
    {
        if (!IsPaused)
        {
            await _engine.StartAsync(AutomationTrigger.Hotkey, profile, LifetimeToken);
        }
    }

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
        foreach (var account in _accountHotkeys)
        {
            account.Hotkey.Unregister();
        }
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
        foreach (var account in _accountHotkeys)
        {
            await account.Hotkey.DisposeAsync();
        }
        _lifetime?.Dispose();
    }

    private CancellationToken LifetimeToken => _lifetime?.Token ?? CancellationToken.None;
}
