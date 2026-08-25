namespace Eslee.OneKey.Core;

public sealed class AutomationCoordinator : IAsyncDisposable
{
    private readonly AutomationSettings _settings;
    private readonly AutomationEngine _engine;
    private readonly IHotkeyService _hotkey;
    private readonly IProcessMonitor _processMonitor;
    private readonly IReadOnlyList<AutomationRuleBinding> _rules;
    private string? _monitoredProcessName;
    private CancellationTokenSource? _lifetime;
    private Task? _restoreTask;

    public AutomationCoordinator(
        AutomationSettings settings,
        AutomationEngine engine,
        IHotkeyService hotkey,
        IProcessMonitor processMonitor,
        IReadOnlyList<AutomationRuleBinding>? rules = null)
    {
        _settings = settings;
        _engine = engine;
        _hotkey = hotkey;
        _processMonitor = processMonitor;
        _rules = rules ?? [];
    }

    /// <summary>
    /// 자동화 규칙 하나와 그 규칙의 전역 단축키입니다. 단축키는 규칙에만 있으므로
    /// 규칙을 늘리면 단축키도 그만큼 늘어납니다. 계정 프로필은 선택 사항입니다.
    /// </summary>
    public sealed record AutomationRuleBinding(
        AutomationSettings Rule,
        IHotkeyService Hotkey,
        GameAccountProfile? Profile = null);

    public bool IsPaused { get; private set; }
    public string? LastError { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _hotkey.Pressed += HandleHotkeyAsync;
        _processMonitor.ProcessStarted += HandleProcessStartedAsync;
        _processMonitor.ProcessExited += HandleProcessExitedAsync;

        // 규칙이 하나도 없을 때만 단독 단축키를 쓴다. 규칙이 있으면 단축키는 규칙에만
        // 있으므로, 같은 조합을 두 번 등록해 Windows가 거부하는 일이 없다.
        if (_rules.Count == 0)
        {
            var registration = await _hotkey.RegisterAsync(_settings.Hotkey, cancellationToken);
            if (!registration.Succeeded)
            {
                LastError = registration.Error ?? "전역 단축키 등록에 실패했습니다.";
            }
        }

        var claimed = new List<HotkeyGesture>();
        foreach (var binding in _rules)
        {
            if (!binding.Rule.Enabled)
            {
                continue;
            }

            if (claimed.Contains(binding.Rule.Hotkey))
            {
                LastError = $"{binding.Rule.Name}이(가) 다른 자동화와 같은 단축키를 씁니다.";
                continue;
            }

            var rule = binding;
            binding.Hotkey.Pressed += () => HandleRuleHotkeyAsync(rule);
            var ruleRegistration = await binding.Hotkey.RegisterAsync(
                binding.Rule.Hotkey,
                cancellationToken);
            if (ruleRegistration.Succeeded)
            {
                claimed.Add(binding.Rule.Hotkey);
            }
            else
            {
                LastError = ruleRegistration.Error ?? $"{binding.Rule.Name} 단축키 등록에 실패했습니다.";
            }
        }

        // 감시 프로세스가 이미 실행 중이면 감시 시작이 곧바로 자동화를 트리거하므로,
        // 이전 세션 복구를 먼저 끝내 진행 중인 실행과 뒤엉키지 않게 한다.
        await _engine.RecoverStaleSessionAsync(cancellationToken);

        await WatchAsync(_settings, cancellationToken);
    }

    public void SetPaused(bool paused) => IsPaused = paused;

    /// <summary>
    /// 규칙의 단축키를 눌렀을 때입니다. 이미 같은 실행 환경으로 자동화가 돌고 있고
    /// 계정만 다르면 계정만 바꾸므로, 사용자가 먼저 자동화를 끝낼 필요가 없습니다.
    /// </summary>
    public async Task HandleRuleHotkeyAsync(AutomationRuleBinding binding)
    {
        if (IsPaused)
        {
            return;
        }

        var result = await _engine.StartRuleAsync(binding.Rule, binding.Profile, LifetimeToken);
        if (result.Started)
        {
            // 규칙마다 감시 대상이 다를 수 있다.
            await WatchAsync(binding.Rule, LifetimeToken);
        }
    }

    /// <summary>감시 대상이 바뀐 경우에만 프로세스 감시를 다시 건다.</summary>
    private async Task WatchAsync(AutomationSettings rule, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rule.WatchProcessName) ||
            string.Equals(_monitoredProcessName, rule.WatchProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_monitoredProcessName is not null)
        {
            await _processMonitor.StopAsync();
        }
        _monitoredProcessName = rule.WatchProcessName;
        await _processMonitor.StartAsync(rule.WatchProcessName, rule.ProcessPollInterval, cancellationToken);
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
        foreach (var binding in _rules)
        {
            binding.Hotkey.Unregister();
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
        foreach (var binding in _rules)
        {
            await binding.Hotkey.DisposeAsync();
        }
        _lifetime?.Dispose();
    }

    private CancellationToken LifetimeToken => _lifetime?.Token ?? CancellationToken.None;
}
