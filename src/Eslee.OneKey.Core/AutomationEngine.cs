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
    private readonly VoiceChannelAutoJoin? _voiceChannelAutoJoin;
    private readonly SemaphoreSlim _gate = new(1, 1);
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
        VoiceChannelAutoJoin? voiceChannelAutoJoin = null)
    {
        _voiceChannelAutoJoin = voiceChannelAutoJoin;
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
            KeptCurrentDevice = false;
            _logger.Info("automation-start", $"자동화를 시작합니다. trigger={trigger}");

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
            await TryJoinVoiceChannelAsync(cancellationToken);

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

    public async Task OnWatchedProcessExitedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State != AutomationState.Active)
            {
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
    /// 음성채널 자동 입장은 부가 기능이므로 어떤 실패도 자동화 시작을 실패시키지
    /// 않습니다. 실행 파일 시작과 오디오 전환은 이미 끝난 뒤에 호출됩니다.
    /// </summary>
    private async Task TryJoinVoiceChannelAsync(CancellationToken cancellationToken)
    {
        if (_voiceChannelAutoJoin is null)
        {
            return;
        }

        try
        {
            var result = await _voiceChannelAutoJoin.EnsureJoinedAsync(_settings, cancellationToken);
            if (!result.IsSuccess)
            {
                LastError = result.Message ?? "Discord 음성채널 자동 입장에 실패했습니다.";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = "Discord 음성채널 자동 입장에 실패했습니다. 다른 자동화 동작은 유지합니다.";
            _logger.Error("voice-join-failed", exception, LastError);
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
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
