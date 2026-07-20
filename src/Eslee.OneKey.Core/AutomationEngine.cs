namespace Eslee.OneKey.Core;

public sealed class AutomationEngine
{
    private static readonly HashSet<AutomationState> BusyStates =
    [
        AutomationState.Starting,
        AutomationState.Active,
        AutomationState.RestorePending,
        AutomationState.Restoring,
    ];

    private readonly AutomationSettings _settings;
    private readonly IAudioEndpointService _audio;
    private readonly IProcessService _processes;
    private readonly IDiscordVoiceStatusClient _discordVoice;
    private readonly ISessionStore _sessions;
    private readonly ISystemClock _clock;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _originalAudioEndpointId;
    private string? _managedAudioEndpointId;

    public AutomationEngine(
        AutomationSettings settings,
        IAudioEndpointService audio,
        IProcessService processes,
        IDiscordVoiceStatusClient discordVoice,
        ISessionStore sessions,
        ISystemClock clock,
        IAppLogger logger)
    {
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
    public event EventHandler? StateChanged;

    public async Task<AutomationStartResult> StartAsync(
        AutomationTrigger trigger,
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

            if (!_settings.Enabled)
            {
                return AutomationStartResult.Ignored("자동화가 비활성화되어 있습니다.");
            }

            SetState(AutomationState.Starting);
            LastRunAt = _clock.UtcNow;
            LastError = null;
            _logger.Info("automation-start", $"자동화를 시작합니다. trigger={trigger}");

            _originalAudioEndpointId = await _audio.GetDefaultOutputIdAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(_originalAudioEndpointId))
            {
                throw new InvalidOperationException("현재 기본 오디오 출력장치를 확인할 수 없습니다.");
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

            await EnsureGameRunningAsync(trigger, cancellationToken);
            await EnsureDiscordRunningAsync(cancellationToken);

            SetState(AutomationState.Active);
            await SaveSessionAsync(cancellationToken);
            return AutomationStartResult.Success();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = exception.Message;
            _logger.Error("automation-start-failed", exception, "자동화 시작에 실패했습니다.");
            await RestoreAfterFailedStartAsync(cancellationToken);
            SetState(AutomationState.Failed);
            return AutomationStartResult.Ignored(exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OnGameExitedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State != AutomationState.Active)
            {
                return;
            }

            _logger.Info("game-exited", "대상 프로세스 종료를 감지했습니다.");
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
            if (string.IsNullOrWhiteSpace(_originalAudioEndpointId))
            {
                LastError = "복원할 원래 오디오 장치가 없습니다.";
                SetState(AutomationState.Failed);
                return;
            }

            SetState(AutomationState.Restoring);
            await _audio.SetDefaultOutputAsync(_originalAudioEndpointId, cancellationToken);
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
        var session = await _sessions.LoadAsync(cancellationToken);
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

    private async Task EnsureGameRunningAsync(
        AutomationTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (await _processes.IsRunningAsync(_settings.GameProcessName, cancellationToken))
        {
            return;
        }

        if (trigger != AutomationTrigger.Hotkey)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.GameExecutablePath))
        {
            throw new InvalidOperationException("게임 실행 파일을 설정해야 합니다.");
        }

        await _processes.StartAsync(_settings.GameExecutablePath, cancellationToken);
        _logger.Info("game-started", "설정된 게임 실행 파일을 시작했습니다.");
    }

    private async Task EnsureDiscordRunningAsync(CancellationToken cancellationToken)
    {
        if (await _processes.IsRunningAsync(_settings.DiscordProcessName, cancellationToken))
        {
            if (_settings.BringDiscordToFront)
            {
                await _processes.BringToFrontAsync(_settings.DiscordProcessName, cancellationToken);
            }
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

    private async Task EvaluateRestoreAsync(CancellationToken cancellationToken)
    {
        if (!_settings.DeferRestoreWhileDiscordInVoice)
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
            await CompleteAsync(cancellationToken, preserveError: true);
            return;
        }

        SetState(AutomationState.Restoring);
        await _audio.SetDefaultOutputAsync(_originalAudioEndpointId, cancellationToken);
        _logger.Info("audio-restored", "원래 오디오 출력장치를 복원했습니다.");
        await CompleteAsync(cancellationToken);
    }

    private async Task RestoreAfterFailedStartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_originalAudioEndpointId) ||
            string.IsNullOrWhiteSpace(_managedAudioEndpointId))
        {
            return;
        }

        try
        {
            var current = await _audio.GetDefaultOutputIdAsync(cancellationToken);
            if (current == _managedAudioEndpointId)
            {
                await _audio.SetDefaultOutputAsync(_originalAudioEndpointId, cancellationToken);
            }
            await _sessions.ClearAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Error("failed-start-restore-failed", exception, "실패 후 오디오 복원도 실패했습니다.");
        }
    }

    private async Task CompleteAsync(
        CancellationToken cancellationToken,
        bool preserveError = false)
    {
        if (!preserveError)
        {
            LastError = null;
        }
        SetState(AutomationState.Completed);
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
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
