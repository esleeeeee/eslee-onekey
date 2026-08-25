namespace Eslee.OneKey.Core;

public sealed class AutomationEngine : IAsyncDisposable
{
    /// <summary>계정 전환으로 다시 띄운 런처의 종료 이벤트를 무시하는 시간입니다.</summary>
    private static readonly TimeSpan LauncherRestartGrace = TimeSpan.FromSeconds(30);

    private static readonly HashSet<AutomationState> BusyStates =
    [
        AutomationState.Starting,
        AutomationState.Active,
        AutomationState.RestorePending,
        AutomationState.Restoring,
    ];

    private AutomationSettings _settings;
    private readonly IAudioEndpointService _audio;
    private readonly IProcessService _processes;
    private readonly IDiscordVoiceStatusClient _discordVoice;
    private readonly ISessionStore _sessions;
    private readonly ISystemClock _clock;
    private readonly IAppLogger _logger;
    private VoiceChannelAutoJoin? _voiceChannelAutoJoin;
    private readonly IGameSessionService? _accountSessions;
    private DateTimeOffset? _launcherRestartedAt;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Discord는 직전 RPC 연결을 닫은 뒤 20초 남짓 새 HANDSHAKE를 받지 않고, 드물게
    /// 그보다 오래 걸린다. 자동화 시작을 붙잡지 않으려면 입장은 백그라운드에서
    /// 재시도해야 하며, 그 재시도에는 분명한 상한이 필요하다.
    /// </summary>
    private static readonly TimeSpan VoiceJoinRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VoiceJoinRetryBudget = TimeSpan.FromMinutes(2);
    private const int VoiceJoinMaxAttempts = 5;

    private CancellationTokenSource? _voiceJoinCancellation;
    private Task? _voiceJoinTask;
    private string? _voiceJoinLastError;
    private bool _disposed;
    private string? _originalAudioEndpointId;
    private string? _managedAudioEndpointId;

    /// <summary>
    /// 이번 시작 직전의 기본 출력 장치입니다. 시작이 실패했을 때 "이번 시작이 바꾼
    /// 것만" 되돌리기 위한 기준이며, 장기 보존되는 복원 대상과는 별개입니다.
    /// </summary>
    private string? _startEntryEndpointId;

    public AutomationEngine(
        AutomationSettings settings,
        IAudioEndpointService audio,
        IProcessService processes,
        IDiscordVoiceStatusClient discordVoice,
        ISessionStore sessions,
        ISystemClock clock,
        IAppLogger logger,
        VoiceChannelAutoJoin? voiceChannelAutoJoin = null,
        IGameSessionService? accountSessions = null)
    {
        _voiceChannelAutoJoin = voiceChannelAutoJoin;
        _accountSessions = accountSessions;
        _settings = settings;
        _audio = audio;
        _processes = processes;
        _discordVoice = discordVoice;
        _sessions = sessions;
        _clock = clock;
        _logger = logger;
    }

    public AutomationState State { get; private set; } = AutomationState.Idle;
    public DateTimeOffset? LastRunAt { get; private set; }
    public string? LastError { get; private set; }
    public bool RestorePending => State == AutomationState.RestorePending;

    /// <summary>
    /// 마지막으로 완료된 자동화가 복원 대신 현재 장치를 유지했는지 여부입니다.
    /// 상태 표시에서 실제 복원과 유지를 구분하는 데 사용합니다.
    /// </summary>
    public bool KeptCurrentDevice { get; private set; }
    public event EventHandler? StateChanged;

    public Task<AutomationStartResult> StartAsync(
        AutomationTrigger trigger,
        CancellationToken cancellationToken = default) =>
        StartAsync(trigger, accountProfile: null, cancellationToken);

    /// <param name="accountProfile">
    /// 실행 전에 활성화할 계정 프로필입니다. null이면 계정을 건드리지 않습니다.
    /// </param>
    public async Task<AutomationStartResult> StartAsync(
        AutomationTrigger trigger,
        GameAccountProfile? accountProfile,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (BusyStates.Contains(State))
            {
                const string reason = "동일 자동화가 이미 실행 중이어서 중복 트리거를 무시했습니다.";
                _logger.Info("duplicate-trigger", reason);
                return AutomationStartResult.Ignored(reason);
            }

            return await StartCoreAsync(trigger, accountProfile, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 규칙마다 Discord 설정이 다를 수 있으므로, 규칙이 바뀌면 그 규칙에 맞는 음성채널
    /// 자동 입장을 다시 만들어 씁니다. 비워 두면 생성자에서 받은 것을 계속 씁니다.
    /// </summary>
    public Func<AutomationSettings, VoiceChannelAutoJoin?>? VoiceChannelAutoJoinFactory { get; set; }

    /// <summary>지금 자동화가 쓰고 있는 규칙입니다.</summary>
    public AutomationSettings ActiveRule => _settings;

    /// <summary>
    /// 자동화 규칙 하나를 시작합니다. 이미 같은 실행 환경으로 돌고 있고 계정만 다르면
    /// 오디오와 Discord는 그대로 둔 채 계정만 바꿉니다. 실행 환경까지 다르면 돌고 있는
    /// 자동화를 건드리지 않고 무시합니다.
    /// </summary>
    public async Task<AutomationStartResult> StartRuleAsync(
        AutomationSettings rule,
        GameAccountProfile? accountProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (BusyStates.Contains(State))
            {
                if (accountProfile is not null && SharesExecutionEnvironment(_settings, rule))
                {
                    _settings = rule;
                    return await SwitchAccountCoreAsync(accountProfile, cancellationToken);
                }

                const string reason = "동일 자동화가 이미 실행 중이어서 중복 트리거를 무시했습니다.";
                _logger.Info("duplicate-trigger", reason);
                return AutomationStartResult.Ignored(reason);
            }

            UseRule(rule);
            return await StartCoreAsync(AutomationTrigger.Hotkey, accountProfile, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void UseRule(AutomationSettings rule)
    {
        _settings = rule;
        if (VoiceChannelAutoJoinFactory is { } factory)
        {
            _voiceChannelAutoJoin = factory(rule);
        }
    }

    /// <summary>
    /// 실행 파일, 감시 프로세스, 오디오 장치, Discord 설정이 모두 같은지 봅니다. 이름과
    /// 단축키, 계정만 다른 규칙이면 환경을 다시 준비할 이유가 없습니다.
    /// </summary>
    private static bool SharesExecutionEnvironment(AutomationSettings a, AutomationSettings b) =>
        Comparable(a) == Comparable(b);

    private static AutomationSettings Comparable(AutomationSettings rule) => rule with
    {
        Id = Guid.Empty,
        Name = string.Empty,
        Enabled = true,
        Hotkey = new HotkeyGesture(),
        AccountProfileId = null,
    };

    /// <summary>
    /// 계정 단축키 전용 진입점입니다. 자동화 환경이 이미 준비돼 있으면 계정만 바꾸고
    /// 오디오와 Discord는 그대로 둡니다. 준비 전이면 평소대로 전체 자동화를 시작합니다.
    /// 계정을 바꾸려고 사용자가 먼저 자동화를 끝낼 필요는 없어야 합니다.
    /// </summary>
    public async Task<AutomationStartResult> StartOrSwitchAccountAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return BusyStates.Contains(State)
                ? await SwitchAccountCoreAsync(profile, cancellationToken)
                : await StartCoreAsync(AutomationTrigger.Hotkey, profile, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 계정만 바꿉니다. 자동화를 시작하지도, 끝내지도 않습니다. 자동화가 실행 중일 때
    /// 계정 단축키가 쓰는 경로와 같습니다. UI의 계정 전환 버튼이 이걸 부릅니다.
    /// </summary>
    public async Task<AutomationStartResult> SwitchAccountAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await SwitchAccountCoreAsync(profile, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 실행 중인 자동화는 그대로 두고 계정만 바꿉니다. 오디오 복원, Discord 재입장,
    /// 환경 재준비는 하지 않습니다.
    /// </summary>
    private async Task<AutomationStartResult> SwitchAccountCoreAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        if (_accountSessions is null)
        {
            return AutomationStartResult.Ignored("계정 전환 기능을 쓸 수 없습니다.");
        }

        try
        {
            var result = await ActivateAccountAsync(profile, cancellationToken);
            if (result.Outcome == GameSessionOutcome.AlreadyActive)
            {
                const string reason = "이미 이 계정이라 런처를 다시 시작하지 않았습니다.";
                _logger.Info("account-already-active", reason);
                return AutomationStartResult.Ignored(reason);
            }

            if (result.Outcome != GameSessionOutcome.Switched)
            {
                // 실행 중인 자동화는 건드리지 않고 실패만 알린다.
                return FailSwitch(result.Message ?? "계정 전환에 실패했습니다.");
            }

            LastError = null;
            await StartLauncherAsync(cancellationToken);
            var confirmation = await _accountSessions.ConfirmActiveAsync(profile, cancellationToken);
            if (confirmation.Outcome == GameSessionOutcome.NeedsEnrollment)
            {
                return FailSwitch(confirmation.Message ?? "런처가 저장된 세션을 거부했습니다.");
            }

            _logger.Info("account-switched", "실행 중인 자동화를 유지한 채 계정을 전환했습니다.");
            RaiseStateChanged();
            return AutomationStartResult.Success();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 런처 실행 파일이 사라졌거나 세션 파일을 읽지 못하는 경우입니다. 실행 중인
            // 자동화를 끝내지는 않되, 조용히 삼키면 사용자는 전환된 줄 알게 됩니다.
            _logger.Error("account-switch-failed", exception, "계정 전환에 실패했습니다.");
            return FailSwitch(exception.Message);
        }
    }

    /// <summary>
    /// 전환 실패를 기록하고 화면에 반영합니다. 자동화 상태는 그대로 두므로 상태 변경
    /// 알림을 따로 보내지 않으면 사용자는 실패를 영영 보지 못합니다.
    /// </summary>
    private AutomationStartResult FailSwitch(string message)
    {
        LastError = message;
        RaiseStateChanged();
        return AutomationStartResult.Ignored(message);
    }

    /// <summary>계정을 바꾼 뒤 런처를 다시 띄웁니다.</summary>
    private async Task StartLauncherAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.LaunchExecutablePath))
        {
            return;
        }

        await _processes.StartAsync(_settings.LaunchExecutablePath, cancellationToken);
        _launcherRestartedAt = _clock.UtcNow;
        _logger.Info("launch-restarted", "계정을 바꾼 뒤 실행 파일을 다시 시작했습니다.");
    }

    private async Task<AutomationStartResult> StartCoreAsync(
        AutomationTrigger trigger,
        GameAccountProfile? accountProfile,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return AutomationStartResult.Ignored("자동화가 비활성화되어 있습니다.");
            }

            SetState(AutomationState.Starting);
            LastRunAt = _clock.UtcNow;
            LastError = null;
            KeptCurrentDevice = false;
            _logger.Info("automation-start", $"자동화를 시작합니다. trigger={trigger}");

            // 계정 전환은 오디오나 실행 파일을 건드리기 전에 끝낸다. 전환할 수 없으면
            // 아무것도 바꾸지 않은 상태로 멈추는 편이 안전하다.
            var account = await ActivateAccountAsync(accountProfile, cancellationToken);
            if (!account.CanContinue)
            {
                LastError = account.Message ?? "계정 전환에 실패했습니다.";
                SetState(AutomationState.Failed);
                return AutomationStartResult.Ignored(LastError);
            }

            var currentEndpointId = await _audio.GetDefaultOutputIdAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(currentEndpointId))
            {
                throw new InvalidOperationException("현재 기본 오디오 출력장치를 확인할 수 없습니다.");
            }

            _startEntryEndpointId = currentEndpointId;

            // 이전 실행에서 전환한 장치가 아직 기본값이면(복원을 건너뛴 경우) 그때의
            // 원래 장치를 계속 복원 대상으로 둔다. 그러지 않으면 두 번째 실행부터
            // 원래 장치 기록이 전환 대상 장치로 덮어써진다.
            if (string.IsNullOrWhiteSpace(_originalAudioEndpointId) ||
                currentEndpointId != _managedAudioEndpointId)
            {
                _originalAudioEndpointId = currentEndpointId;
            }

            var endpoints = await _audio.GetOutputEndpointsAsync(cancellationToken);
            var target = endpoints.FirstOrDefault(endpoint =>
                endpoint.IsActive && endpoint.Id == _settings.TargetAudioEndpointId);
            if (target is null)
            {
                throw new InvalidOperationException("지정한 헤드셋이 연결되어 있지 않습니다.");
            }

            _logger.Info("audio-original-saved", "원래 기본 오디오 장치를 세션에 저장했습니다.");
            await _audio.SetDefaultOutputAsync(target.Id, cancellationToken);
            _managedAudioEndpointId = target.Id;
            _logger.Info("audio-switched", "지정한 헤드셋으로 기본 출력을 전환했습니다.");
            await SaveSessionAsync(cancellationToken);

            await EnsureLaunchTargetRunningAsync(trigger, cancellationToken);
            await EnsureDiscordRunningAsync(cancellationToken);
            // 음성채널 입장은 Discord 재연결을 기다릴 수 있으므로 자동화 시작을
            // 붙잡지 않는다. 실행 파일 시작과 오디오 전환은 이미 끝난 상태다.
            StartVoiceChannelJoin(cancellationToken);

            SetState(AutomationState.Active);
            await SaveSessionAsync(cancellationToken);
            return AutomationStartResult.Success();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CancelPendingVoiceJoin();
            LastError = exception.Message;
            _logger.Error("automation-start-failed", exception, "자동화 시작에 실패했습니다.");
            await RestoreAfterFailedStartAsync(cancellationToken);
            SetState(AutomationState.Failed);
            return AutomationStartResult.Ignored(exception.Message);
        }
    }

    public async Task OnWatchedProcessExitedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State != AutomationState.Active)
            {
                return;
            }

            // 감시 대상이 런처와 같은 설정에서는 계정 전환이 종료 이벤트를 만든다.
            // 우리가 방금 닫았다 다시 띄운 것이고 지금 살아 있다면 종료가 아니다.
            // 그러지 않으면 계정 전환이 오디오 복원과 자동화 종료를 불러온다.
            if (_launcherRestartedAt is { } restartedAt &&
                _clock.UtcNow - restartedAt < LauncherRestartGrace &&
                !string.IsNullOrWhiteSpace(_settings.WatchProcessName) &&
                await _processes.IsRunningAsync(_settings.WatchProcessName, cancellationToken))
            {
                _launcherRestartedAt = null;
                _logger.Info(
                    "watched-process-restarted-by-switch",
                    "계정 전환으로 다시 띄운 것이라 종료로 처리하지 않았습니다.");
                return;
            }

            _logger.Info("watched-process-exited", "대상 프로세스 종료를 감지했습니다.");
            if (!_settings.RestoreAudioOnExit)
            {
                // 자동 복원이 꺼져 있으면 오디오를 그대로 두고 자동화를 끝낸다.
                // Discord 통화 확인이나 복원 대기 상태로도 들어가지 않는다.
                _logger.Info("restore-skipped", "자동 복원이 꺼져 있어 현재 오디오 장치를 유지합니다.");
                KeptCurrentDevice = true;
                await CompleteAsync(cancellationToken, keepRestoreTarget: true);
                return;
            }

            await EvaluateRestoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ContinuePendingRestoreAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State == AutomationState.RestorePending)
            {
                await EvaluateRestoreAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WaitForRestoreAsync(CancellationToken cancellationToken)
    {
        var delay = _settings.RestorePollInterval;
        while (State == AutomationState.RestorePending)
        {
            await _clock.DelayAsync(delay, cancellationToken);
            await ContinuePendingRestoreAsync(cancellationToken);
            delay = LastError is null
                ? _settings.RestorePollInterval
                : TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    public async Task ManualRestoreAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CancelPendingVoiceJoin();
            if (string.IsNullOrWhiteSpace(_originalAudioEndpointId))
            {
                LastError = "복원할 원래 오디오 장치가 없습니다.";
                SetState(AutomationState.Failed);
                return;
            }

            SetState(AutomationState.Restoring);
            if (!await TrySetDefaultOutputAsync(_originalAudioEndpointId, cancellationToken))
            {
                // 복원 대상을 유지한 채 오류만 알린다. 장치를 다시 연결하면 재시도할 수 있다.
                SetState(AutomationState.Failed);
                return;
            }

            KeptCurrentDevice = false;
            await CompleteAsync(cancellationToken);
            _logger.Info("manual-restore", "사용자 요청으로 원래 오디오 장치를 복원했습니다.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task KeepCurrentAndStopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LastError = null;
            KeptCurrentDevice = true;
            await CompleteAsync(cancellationToken);
            _logger.Info("automation-stopped", "현재 오디오 장치를 유지하고 자동화를 종료했습니다.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecoverStaleSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // 이미 이 엔진에서 자동화가 시작됐다면 진행 중인 상태를 덮어쓰지 않는다.
            if (State != AutomationState.Idle)
            {
                return;
            }

            await RecoverSessionCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RecoverSessionCoreAsync(CancellationToken cancellationToken)
    {
        var session = await _sessions.LoadAsync(cancellationToken);
        if (session is { State: AutomationState.Completed })
        {
            // 복원을 건너뛰고 현재 장치를 유지한 채 끝난 세션입니다. 오디오는 그대로
            // 두고, 사용자가 수동 복원을 누를 수 있도록 대상만 되살립니다.
            _originalAudioEndpointId = session.OriginalAudioEndpointId;
            _managedAudioEndpointId = session.ManagedAudioEndpointId;
            LastRunAt = session.LastRunAt;
            KeptCurrentDevice = true;
            return;
        }

        if (session is null || !BusyStates.Contains(session.State))
        {
            return;
        }

        _originalAudioEndpointId = session.OriginalAudioEndpointId;
        _managedAudioEndpointId = session.ManagedAudioEndpointId;
        LastRunAt = session.LastRunAt;
        LastError =
            "이전 실행이 비정상 종료되었습니다. 오디오는 자동 변경하지 않았습니다. " +
            "현재 상태를 확인한 뒤 수동 복원 또는 현재 장치 유지를 선택하세요.";
        SetState(AutomationState.Failed);
        _logger.Warning("stale-session", LastError);
    }

    private async Task EnsureLaunchTargetRunningAsync(
        AutomationTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (await _processes.IsRunningAsync(_settings.WatchProcessName, cancellationToken))
        {
            return;
        }

        if (trigger != AutomationTrigger.Hotkey)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.LaunchExecutablePath))
        {
            throw new InvalidOperationException("실행 파일을 설정해야 합니다.");
        }

        await _processes.StartAsync(_settings.LaunchExecutablePath, cancellationToken);
        _logger.Info("launch-started", "설정된 실행 파일을 시작했습니다.");
    }

    private async Task EnsureDiscordRunningAsync(CancellationToken cancellationToken)
    {
        if (!_settings.UseDiscordIntegration)
        {
            return;
        }

        if (await _processes.IsRunningAsync(_settings.DiscordProcessName, cancellationToken))
        {
            // 이미 실행 중이면 창을 전면으로 가져오지 않고 그대로 둔다.
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.DiscordExecutablePath))
        {
            LastError = "Discord가 실행 중이 아니지만 실행 파일 경로가 설정되지 않았습니다.";
            _logger.Warning("discord-launch-skipped", LastError);
            return;
        }

        try
        {
            await _processes.StartAsync(_settings.DiscordExecutablePath, cancellationToken);
            _logger.Info("discord-started", "설정된 Discord 실행 파일을 시작했습니다.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = "Discord 실행에 실패했습니다. 다른 자동화 동작은 유지합니다.";
            _logger.Error("discord-launch-failed", exception, LastError);
        }
    }

    /// <summary>
    /// 지정한 계정 프로필의 로그인 세션을 활성화합니다. 프로필이 없거나 이미 그
    /// 계정이면 아무것도 하지 않습니다.
    /// </summary>
    private async Task<GameSessionResult> ActivateAccountAsync(
        GameAccountProfile? profile,
        CancellationToken cancellationToken)
    {
        if (profile is null || _accountSessions is null)
        {
            return new GameSessionResult(GameSessionOutcome.NotRequested);
        }

        var result = await _accountSessions.ActivateAsync(profile, cancellationToken);
        _logger.Info("account-activation", $"계정 전환 결과: {result.Outcome}");
        return result;
    }

    /// <summary>
    /// 음성채널 입장을 백그라운드에서 시작합니다. Discord는 직전 RPC 연결을 닫은 뒤
    /// 한동안 새 연결을 받지 않으므로, 여기서 기다리면 자동화 시작 자체가 늦어집니다.
    /// 이전 세션의 재시도가 남지 않도록 새로 시작할 때마다 앞의 작업을 취소합니다.
    /// </summary>
    private void StartVoiceChannelJoin(CancellationToken cancellationToken)
    {
        if (_voiceChannelAutoJoin is null)
        {
            return;
        }

        CancelPendingVoiceJoin();
        if (_disposed)
        {
            return;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _voiceJoinCancellation = source;
        _voiceJoinTask = RunVoiceChannelJoinAsync(source);
    }

    /// <summary>
    /// 연결이 열릴 때까지 제한된 횟수와 시간 안에서만 다시 시도합니다. 어떤 실패도
    /// 자동화를 실패시키지 않고 마지막 오류로만 남습니다.
    /// </summary>
    private async Task RunVoiceChannelJoinAsync(CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            var deadline = _clock.UtcNow + VoiceJoinRetryBudget;
            for (var attempt = 1; attempt <= VoiceJoinMaxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var retryable = await TryJoinVoiceChannelOnceAsync(attempt, token);
                if (!retryable)
                {
                    return;
                }

                if (attempt == VoiceJoinMaxAttempts || _clock.UtcNow + VoiceJoinRetryDelay >= deadline)
                {
                    _logger.Warning(
                        "voice-join-gave-up",
                        $"음성채널 자동 입장을 {attempt}회 시도 후 중단했습니다.");
                    return;
                }

                await _clock.DelayAsync(VoiceJoinRetryDelay, token);
            }
        }
        catch (OperationCanceledException)
        {
            // 새 자동화 세션이 시작됐거나 앱이 종료 중입니다. 조용히 끝냅니다.
        }
        finally
        {
            source.Dispose();
        }
    }

    /// <summary>한 번 시도하고, 다시 시도할 가치가 있으면 true를 돌려줍니다.</summary>
    private async Task<bool> TryJoinVoiceChannelOnceAsync(int attempt, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _voiceChannelAutoJoin!.EnsureJoinedAsync(_settings, cancellationToken);
            if (result.IsSuccess)
            {
                // 이미 대상 채널이거나 다른 채널에 있어 건너뛴 경우도 여기서 끝납니다.
                ClearVoiceJoinError();
                return false;
            }

            SetVoiceJoinError(result.Message ?? "Discord 음성채널 자동 입장에 실패했습니다.");

            // 설정·인증 문제는 다시 시도해도 같은 결과이므로 즉시 멈춥니다.
            return result.Outcome == VoiceJoinOutcome.DiscordUnavailable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("voice-join-failed", exception, $"음성채널 입장 {attempt}회차 실패");
            SetVoiceJoinError("Discord 음성채널 자동 입장에 실패했습니다. 다른 자동화 동작은 유지합니다.");
            return true;
        }
    }

    /// <summary>
    /// 입장 오류는 부가 기능의 오류이므로, 나중에 입장에 성공하면 되돌립니다.
    /// 자동화 본체가 남긴 오류 문구는 건드리지 않습니다.
    /// </summary>
    private void SetVoiceJoinError(string message)
    {
        LastError = message;
        _voiceJoinLastError = message;
        RaiseStateChanged();
    }

    private void ClearVoiceJoinError()
    {
        if (_voiceJoinLastError is null)
        {
            return;
        }

        if (LastError == _voiceJoinLastError)
        {
            LastError = null;
            RaiseStateChanged();
        }
        _voiceJoinLastError = null;
    }

    private void CancelPendingVoiceJoin()
    {
        try
        {
            _voiceJoinCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 이미 끝난 재시도입니다.
        }
        _voiceJoinCancellation = null;
    }

    /// <summary>진행 중인 음성채널 입장 재시도가 끝날 때까지 기다립니다(진단·시험용).</summary>
    public Task WaitForVoiceChannelJoinAsync() => _voiceJoinTask ?? Task.CompletedTask;

    /// <summary>앱 종료 시 남은 재시도를 정리합니다.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        CancelPendingVoiceJoin();
        var pending = _voiceJoinTask;
        _voiceJoinTask = null;
        if (pending is not null)
        {
            try
            {
                await pending;
            }
            catch (Exception)
            {
                // 정리 경로는 무엇이 실패하든 계속 진행해야 합니다. 재시도 태스크의
                // 예외가 앱 종료를 깨뜨리지 않도록 여기서 흡수합니다.
            }
        }
    }

    private async Task EvaluateRestoreAsync(CancellationToken cancellationToken)
    {
        if (!_settings.UseDiscordIntegration || !_settings.DeferRestoreWhileDiscordInVoice)
        {
            await RestoreIfSafeAsync(cancellationToken);
            return;
        }

        var voice = await _discordVoice.CheckAsync(cancellationToken);
        switch (voice.State)
        {
            case DiscordVoiceState.NotInVoice:
                LastError = null;
                await RestoreIfSafeAsync(cancellationToken);
                break;
            case DiscordVoiceState.InVoice:
                LastError = null;
                SetState(AutomationState.RestorePending);
                await SaveSessionAsync(cancellationToken);
                _logger.Info("restore-pending", "Discord 통화 종료까지 헤드셋을 유지합니다.");
                break;
            case DiscordVoiceState.Unauthorized:
                LastError = voice.Error ?? "Discord API 인증 설정을 확인하세요.";
                await EnterRestorePendingAsync(cancellationToken);
                break;
            case DiscordVoiceState.NotReady:
            case DiscordVoiceState.Unavailable:
                LastError = voice.Error ?? "Discord 음성 상태를 확인할 수 없습니다.";
                await EnterRestorePendingAsync(cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private async Task EnterRestorePendingAsync(CancellationToken cancellationToken)
    {
        SetState(AutomationState.RestorePending);
        await SaveSessionAsync(cancellationToken);
        _logger.Warning("restore-pending-api-error", LastError ?? "Discord API 오류");
    }

    private async Task RestoreIfSafeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_originalAudioEndpointId) ||
            string.IsNullOrWhiteSpace(_managedAudioEndpointId))
        {
            LastError = "복원 세션의 오디오 장치 정보가 없습니다.";
            SetState(AutomationState.Failed);
            return;
        }

        var current = await _audio.GetDefaultOutputIdAsync(cancellationToken);
        if (current != _managedAudioEndpointId)
        {
            LastError = "사용자가 오디오 장치를 수동 변경하여 자동 복원을 취소했습니다.";
            _logger.Warning("restore-cancelled-user-change", LastError);
            KeptCurrentDevice = true;
            await CompleteAsync(cancellationToken, preserveError: true);
            return;
        }

        SetState(AutomationState.Restoring);
        if (!await TrySetDefaultOutputAsync(_originalAudioEndpointId, cancellationToken))
        {
            SetState(AutomationState.Failed);
            return;
        }

        _logger.Info("audio-restored", "원래 오디오 출력장치를 복원했습니다.");
        KeptCurrentDevice = false;
        await CompleteAsync(cancellationToken);
    }

    /// <summary>
    /// 복원 대상이 아직 연결돼 있을 때만 기본 출력으로 지정합니다. 장치가 사라졌거나
    /// 전환이 실패해도 예외를 밖으로 던지지 않고 오류로만 알립니다. 복원 대상은
    /// 앱 재시작을 넘어 살아남을 수 있어 그 사이 장치가 제거될 수 있습니다.
    /// </summary>
    private async Task<bool> TrySetDefaultOutputAsync(
        string endpointId,
        CancellationToken cancellationToken)
    {
        try
        {
            var endpoints = await _audio.GetOutputEndpointsAsync(cancellationToken);
            if (!endpoints.Any(endpoint => endpoint.IsActive && endpoint.Id == endpointId))
            {
                LastError = "복원할 오디오 장치가 연결되어 있지 않습니다. 장치를 연결한 뒤 다시 시도하세요.";
                _logger.Warning("restore-target-unavailable", LastError);
                return false;
            }

            await _audio.SetDefaultOutputAsync(endpointId, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = "오디오 장치 복원에 실패했습니다.";
            _logger.Error("restore-failed", exception, LastError);
            return false;
        }
    }

    /// <summary>
    /// 시작이 실패했을 때 이번 시작이 실제로 바꾼 오디오 변경만 되돌립니다. 이전
    /// 실행에서 유지하기로 한 장치는 건드리지 않으며, 그 복원 대상도 그대로 둡니다.
    /// </summary>
    private async Task RestoreAfterFailedStartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_startEntryEndpointId) ||
            string.IsNullOrWhiteSpace(_managedAudioEndpointId))
        {
            return;
        }

        try
        {
            var current = await _audio.GetDefaultOutputIdAsync(cancellationToken);
            if (current != _managedAudioEndpointId || current == _startEntryEndpointId)
            {
                // 이번 시작이 기본 장치를 바꾸지 않았다. 되돌릴 것이 없다.
                return;
            }

            await _audio.SetDefaultOutputAsync(_startEntryEndpointId, cancellationToken);
            _logger.Info("failed-start-rolled-back", "시작 실패로 이번에 바꾼 오디오 장치를 되돌렸습니다.");
            _originalAudioEndpointId = null;
            _managedAudioEndpointId = null;
            await _sessions.ClearAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Error("failed-start-restore-failed", exception, "실패 후 오디오 복원도 실패했습니다.");
        }
    }

    /// <param name="keepRestoreTarget">
    /// 자동 복원을 건너뛴 경우에도 사용자가 "지금 원래 장치로 복원"을 누를 수 있도록
    /// 원래 장치 정보를 남겨 둡니다. 앱을 다시 시작해도 남아 있도록 세션에 기록합니다.
    /// </param>
    private async Task CompleteAsync(
        CancellationToken cancellationToken,
        bool preserveError = false,
        bool keepRestoreTarget = false)
    {
        if (!preserveError)
        {
            LastError = null;
        }
        // 끝난 세션이 뒤늦게 사용자를 음성채널에 밀어 넣지 않도록 재시도를 멈춘다.
        CancelPendingVoiceJoin();
        SetState(AutomationState.Completed);
        if (keepRestoreTarget)
        {
            await SaveSessionAsync(cancellationToken);
            return;
        }

        await _sessions.ClearAsync(cancellationToken);
        _originalAudioEndpointId = null;
        _managedAudioEndpointId = null;
    }

    private Task SaveSessionAsync(CancellationToken cancellationToken) =>
        _sessions.SaveAsync(
            new AutomationSession
            {
                State = State,
                OriginalAudioEndpointId = _originalAudioEndpointId,
                ManagedAudioEndpointId = _managedAudioEndpointId,
                LastRunAt = LastRunAt,
            },
            cancellationToken);

    private void SetState(AutomationState state)
    {
        State = state;
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
