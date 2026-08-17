using Eslee.OneKey.Core;

namespace Eslee.OneKey.Tests;

public sealed class AutomationEngineTests
{
    [Fact]
    public async Task HotkeyStartsAppsSwitchesAudioAndRestoresWhenNotInVoice()
    {
        var harness = Harness.Create(DiscordVoiceState.NotInVoice);

        var result = await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnWatchedProcessExitedAsync();

        Assert.True(result.Started);
        Assert.Equal(["game.exe", "discord.exe"], harness.Processes.StartedPaths);
        Assert.Equal(["headset", "speaker"], harness.Audio.SetCalls);
        Assert.Equal(AutomationState.Completed, harness.Engine.State);
    }

    [Fact]
    public async Task AlreadyRunningWatchedProcessIsNotStartedAgain()
    {
        var harness = Harness.Create();
        harness.Processes.Running.Add("game");

        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.DoesNotContain("game.exe", harness.Processes.StartedPaths);
        Assert.Contains("discord.exe", harness.Processes.StartedPaths);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
    }

    [Fact]
    public async Task ProcessTriggerAfterHotkeyIsIgnoredWhileActive()
    {
        var harness = Harness.Create();
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        var duplicate = await harness.Engine.StartAsync(AutomationTrigger.ProcessStarted);

        Assert.False(duplicate.Started);
        Assert.Single(harness.Audio.SetCalls);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
    }

    [Fact]
    public async Task WatchedProcessExitDuringDiscordVoiceEntersRestorePending()
    {
        var harness = Harness.Create(DiscordVoiceState.InVoice);
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        await harness.Engine.OnWatchedProcessExitedAsync();

        Assert.Equal(AutomationState.RestorePending, harness.Engine.State);
        Assert.Equal("headset", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task PendingRestoreCompletesAfterDiscordVoiceEnds()
    {
        var harness = Harness.Create(DiscordVoiceState.InVoice, DiscordVoiceState.NotInVoice);
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnWatchedProcessExitedAsync();

        await harness.Engine.ContinuePendingRestoreAsync();

        Assert.Equal(AutomationState.Completed, harness.Engine.State);
        Assert.Equal("speaker", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task UserAudioChangeCancelsAutomaticRestore()
    {
        var harness = Harness.Create(DiscordVoiceState.InVoice, DiscordVoiceState.NotInVoice);
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnWatchedProcessExitedAsync();
        harness.Audio.DefaultId = "earbuds";

        await harness.Engine.ContinuePendingRestoreAsync();

        Assert.Equal("earbuds", harness.Audio.DefaultId);
        Assert.Equal(AutomationState.Completed, harness.Engine.State);
        Assert.Contains("수동 변경", harness.Engine.LastError);
    }

    [Fact]
    public async Task ApiTimeoutKeepsHeadsetAndRetriesSafely()
    {
        var harness = Harness.Create(DiscordVoiceState.Unavailable, DiscordVoiceState.NotInVoice);
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        await harness.Engine.OnWatchedProcessExitedAsync();

        Assert.Equal(AutomationState.RestorePending, harness.Engine.State);
        Assert.Equal("headset", harness.Audio.DefaultId);

        await harness.Engine.ContinuePendingRestoreAsync();
        Assert.Equal("speaker", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task AuthenticationFailureIsVisibleWithoutSecretLeak()
    {
        var harness = Harness.Create(DiscordVoiceState.Unauthorized);
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        await harness.Engine.OnWatchedProcessExitedAsync();

        Assert.Equal(AutomationState.RestorePending, harness.Engine.State);
        Assert.Contains("인증", harness.Engine.LastError);
        Assert.DoesNotContain("secret", string.Join(Environment.NewLine, harness.Logger.Messages));
    }

    [Fact]
    public async Task HotkeyCollisionDoesNotCrashProcessMonitoring()
    {
        var harness = Harness.Create();
        var hotkey = new FakeHotkeyService
        {
            Registration = new HotkeyRegistrationResult(false, "hotkey collision"),
        };
        var monitor = new FakeProcessMonitor();
        await using var coordinator = new AutomationCoordinator(
            harness.Settings,
            harness.Engine,
            hotkey,
            monitor);

        await coordinator.InitializeAsync();

        Assert.Equal("hotkey collision", coordinator.LastError);
        Assert.True(monitor.Started);
    }

    [Fact]
    public async Task DisconnectedTargetHeadsetFailsWithoutChangingAudio()
    {
        var harness = Harness.Create();
        harness.Audio.Endpoints =
        [
            new AudioEndpoint("speaker", "Speakers", true),
            new AudioEndpoint("headset", "Headset", false),
        ];

        var result = await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.False(result.Started);
        Assert.Equal("speaker", harness.Audio.DefaultId);
        Assert.Empty(harness.Audio.SetCalls);
        Assert.Equal(AutomationState.Failed, harness.Engine.State);
    }

    [Fact]
    public async Task AlreadyRunningDiscordIsNotStartedAgainOrBroughtToFront()
    {
        var harness = Harness.Create();
        harness.Processes.Running.Add("discord");

        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.DoesNotContain("discord.exe", harness.Processes.StartedPaths);
        Assert.Empty(harness.Processes.BroughtToFront);
    }

    [Fact]
    public async Task StaleActiveSessionNeverChangesAudioAutomatically()
    {
        var harness = Harness.Create();
        harness.Sessions.Session = new AutomationSession
        {
            State = AutomationState.Active,
            OriginalAudioEndpointId = "speaker",
            ManagedAudioEndpointId = "headset",
            LastRunAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        };
        harness.Audio.DefaultId = "headset";

        await harness.Engine.RecoverStaleSessionAsync();

        Assert.Equal(AutomationState.Failed, harness.Engine.State);
        Assert.Empty(harness.Audio.SetCalls);
        Assert.Contains("비정상 종료", harness.Engine.LastError);
    }

    [Fact]
    public async Task AutomationWithoutDiscordIntegrationStartsAndRestoresImmediately()
    {
        var harness = Harness.CreateWithoutDiscord();

        var result = await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.True(result.Started);
        Assert.Null(harness.Engine.LastError);
        Assert.Equal(["game.exe"], harness.Processes.StartedPaths);
        Assert.Equal(AutomationState.Active, harness.Engine.State);

        await harness.Engine.OnWatchedProcessExitedAsync();

        Assert.Equal(AutomationState.Completed, harness.Engine.State);
        Assert.Equal("speaker", harness.Audio.DefaultId);
        Assert.Equal(0, harness.Voice.Calls);
    }

    [Fact]
    public async Task RestoreDisabledKeepsCurrentDeviceAndSkipsVoiceCheck()
    {
        // Discord 통화 중 상태를 넣어 두어도 복원이 꺼져 있으면 조회조차 하지 않는다.
        var harness = Harness.CreateWithoutRestore(DiscordVoiceState.InVoice);
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        await harness.Engine.OnWatchedProcessExitedAsync();

        Assert.Equal(AutomationState.Completed, harness.Engine.State);
        Assert.False(harness.Engine.RestorePending);
        Assert.Equal("headset", harness.Audio.DefaultId);
        Assert.Equal(["headset"], harness.Audio.SetCalls);
        Assert.Equal(0, harness.Voice.Calls);
        Assert.Null(harness.Engine.LastError);
    }

    [Fact]
    public async Task RestoreDisabledDoesNotStartRestorePollingFromCoordinator()
    {
        var harness = Harness.CreateWithoutRestore(DiscordVoiceState.InVoice);
        await using var coordinator = new AutomationCoordinator(
            harness.Settings,
            harness.Engine,
            new FakeHotkeyService(),
            new FakeProcessMonitor());
        await coordinator.InitializeAsync();
        await coordinator.HandleHotkeyAsync();

        await coordinator.HandleProcessExitedAsync();

        Assert.Equal(AutomationState.Completed, harness.Engine.State);
        Assert.False(harness.Engine.RestorePending);
        Assert.Equal("headset", harness.Audio.DefaultId);
        Assert.Equal(0, harness.Voice.Calls);
    }

    [Fact]
    public async Task RestoreDisabledStillAllowsManualRestore()
    {
        var harness = Harness.CreateWithoutRestore();
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnWatchedProcessExitedAsync();

        await harness.Engine.ManualRestoreAsync();

        Assert.Equal("speaker", harness.Audio.DefaultId);
        Assert.Equal(AutomationState.Completed, harness.Engine.State);
    }

    [Fact]
    public async Task RestoreDisabledKeepsOriginalDeviceAcrossRepeatedRuns()
    {
        // 2회차 실행 시점의 기본 장치는 1회차에서 전환한 헤드셋이다. 이때
        // 원래 장치 기록을 덮어쓰면 수동 복원이 무동작이 된다.
        var harness = Harness.CreateWithoutRestore();
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnWatchedProcessExitedAsync();
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnWatchedProcessExitedAsync();

        await harness.Engine.ManualRestoreAsync();

        Assert.Equal("speaker", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task RestoreDisabledUsesCurrentDeviceAsOriginalWhenUserChangedItBetweenRuns()
    {
        var harness = Harness.CreateWithoutRestore();
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnWatchedProcessExitedAsync();
        harness.Audio.DefaultId = "earbuds";

        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnWatchedProcessExitedAsync();
        await harness.Engine.ManualRestoreAsync();

        Assert.Equal("earbuds", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task RestoreDisabledKeepsManualRestoreTargetAfterEngineRestart()
    {
        var harness = Harness.CreateWithoutRestore();
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnWatchedProcessExitedAsync();

        // 앱 재시작이나 설정 저장으로 엔진이 새로 만들어져도 세션에서 되살린다.
        var restarted = harness.CreateReplacementEngine();
        await restarted.RecoverStaleSessionAsync();

        Assert.Equal(AutomationState.Idle, restarted.State);
        Assert.Empty(harness.Audio.SetCalls.Skip(1));
        await restarted.ManualRestoreAsync();
        Assert.Equal("speaker", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task CompletedStateDistinguishesKeptDeviceFromActualRestore()
    {
        var kept = Harness.CreateWithoutRestore();
        await kept.Engine.StartAsync(AutomationTrigger.Hotkey);
        await kept.Engine.OnWatchedProcessExitedAsync();

        var restored = Harness.Create(DiscordVoiceState.NotInVoice);
        await restored.Engine.StartAsync(AutomationTrigger.Hotkey);
        await restored.Engine.OnWatchedProcessExitedAsync();

        Assert.True(kept.Engine.KeptCurrentDevice);
        Assert.Equal(
            "현재 장치 유지",
            AutomationStatusText.ForState(kept.Engine.State, false, kept.Engine.KeptCurrentDevice));
        Assert.False(restored.Engine.KeptCurrentDevice);
        Assert.Equal(
            "복원 완료",
            AutomationStatusText.ForState(
                restored.Engine.State,
                false,
                restored.Engine.KeptCurrentDevice));
    }

    [Fact]
    public async Task FailedStartAfterKeptDeviceDoesNotUndoTheKeptSwitch()
    {
        // 유지 완료 뒤 두 번째 시작이 실패해도 이번 시작이 바꾼 것이 없으므로
        // 오디오를 되돌리면 안 된다(자동 복원을 꺼 둔 사용자의 선택 위반).
        var harness = Harness.CreateWithoutRestore();
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnWatchedProcessExitedAsync();
        harness.Processes.Running.Remove("game");
        harness.Processes.FailNextStart = true;

        var result = await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.False(result.Started);
        Assert.Equal(AutomationState.Failed, harness.Engine.State);
        Assert.Equal("headset", harness.Audio.DefaultId);
        Assert.DoesNotContain("speaker", harness.Audio.SetCalls);

        // 유지해 둔 복원 대상도 살아 있어야 한다.
        await harness.Engine.ManualRestoreAsync();
        Assert.Equal("speaker", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task FailedStartRollsBackOnlyWhatThisStartChanged()
    {
        var harness = Harness.CreateWithoutRestore();
        harness.Processes.FailNextStart = true;

        var result = await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.False(result.Started);
        Assert.Equal(AutomationState.Failed, harness.Engine.State);
        Assert.Equal("speaker", harness.Audio.DefaultId);
        Assert.Equal(["headset", "speaker"], harness.Audio.SetCalls);
    }

    [Fact]
    public async Task RestoreReportsErrorWhenOriginalDeviceIsGone()
    {
        var harness = Harness.Create(DiscordVoiceState.NotInVoice);
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        // 복원 직전에 원래 장치가 시스템에서 사라진 상황.
        harness.Audio.Endpoints = [new AudioEndpoint("headset", "Headset", true)];

        await harness.Engine.OnWatchedProcessExitedAsync();

        Assert.Equal(AutomationState.Failed, harness.Engine.State);
        Assert.Contains("연결", harness.Engine.LastError);
        Assert.Equal("headset", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task ManualRestoreReportsErrorInsteadOfThrowingWhenSwitchFails()
    {
        var harness = Harness.CreateWithoutRestore();
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnWatchedProcessExitedAsync();
        harness.Audio.ThrowOnSet = true;

        await harness.Engine.ManualRestoreAsync();

        Assert.Equal(AutomationState.Failed, harness.Engine.State);
        Assert.Equal("headset", harness.Audio.DefaultId);
        Assert.NotNull(harness.Engine.LastError);
    }

    [Fact]
    public async Task SessionRecoveryNeverOverwritesAnAutomationAlreadyRunning()
    {
        var harness = Harness.CreateWithoutRestore();
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        harness.Sessions.Session = new AutomationSession
        {
            State = AutomationState.Completed,
            OriginalAudioEndpointId = "earbuds",
            ManagedAudioEndpointId = "earbuds",
        };

        await harness.Engine.RecoverStaleSessionAsync();

        Assert.Equal(AutomationState.Active, harness.Engine.State);
        Assert.False(harness.Engine.KeptCurrentDevice);
        await harness.Engine.OnWatchedProcessExitedAsync();
        await harness.Engine.ManualRestoreAsync();
        Assert.Equal("speaker", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task CoordinatorRecoversBeforeAnAlreadyRunningProcessTriggersAutomation()
    {
        var harness = Harness.CreateWithoutRestore();
        harness.Processes.Running.Add("game");
        harness.Sessions.Session = new AutomationSession
        {
            State = AutomationState.Completed,
            OriginalAudioEndpointId = "earbuds",
            ManagedAudioEndpointId = "earbuds",
        };
        var monitor = new FakeProcessMonitor { RaiseStartedOnStart = true };
        await using var coordinator = new AutomationCoordinator(
            harness.Settings,
            harness.Engine,
            new FakeHotkeyService(),
            monitor);

        await coordinator.InitializeAsync();

        // 감시 시작이 즉시 트리거한 실행이 복구 로직에 뒤집히지 않아야 한다.
        Assert.Equal(AutomationState.Active, harness.Engine.State);
        Assert.Null(harness.Engine.LastError);
        Assert.Equal("headset", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task DiscordIntegrationStillRunsWhenRestoreDisabled()
    {
        var harness = Harness.CreateWithoutRestore();

        var result = await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.True(result.Started);
        Assert.Equal(["game.exe", "discord.exe"], harness.Processes.StartedPaths);
        Assert.Equal("headset", harness.Audio.DefaultId);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
    }

    private sealed class Harness
    {
        private Harness(AutomationSettings settings, DiscordVoiceState[] states)
        {
            Settings = settings;
            Audio = new FakeAudioService();
            Processes = new FakeProcessService();
            Voice = new FakeVoiceClient(states);
            Sessions = new FakeSessionStore();
            Logger = new FakeLogger();
            Engine = new AutomationEngine(
                Settings,
                Audio,
                Processes,
                Voice,
                Sessions,
                new FakeClock(),
                Logger);
        }

        public AutomationSettings Settings { get; }
        public FakeAudioService Audio { get; }
        public FakeProcessService Processes { get; }
        public FakeVoiceClient Voice { get; }
        public FakeSessionStore Sessions { get; }
        public FakeLogger Logger { get; }
        public AutomationEngine Engine { get; }

        /// <summary>앱 재시작이나 설정 저장으로 엔진만 새로 만들어진 상황을 재현합니다.</summary>
        public AutomationEngine CreateReplacementEngine() => new(
            Settings,
            Audio,
            Processes,
            Voice,
            Sessions,
            new FakeClock(),
            Logger);

        /// <summary>자동 복원을 켠 구성입니다. 기존 안전 복원 동작을 검증합니다.</summary>
        public static Harness Create(params DiscordVoiceState[] states) => new(
            new AutomationSettings
            {
                WatchProcessName = "game",
                LaunchExecutablePath = "game.exe",
                UseDiscordIntegration = true,
                DiscordProcessName = "discord",
                DiscordExecutablePath = "discord.exe",
                TargetAudioEndpointId = "headset",
                DiscordApiBaseUrl = "https://example.invalid",
                RestoreAudioOnExit = true,
            },
            states);

        public static Harness CreateWithoutDiscord() => new(
            new AutomationSettings
            {
                WatchProcessName = "game",
                LaunchExecutablePath = "game.exe",
                UseDiscordIntegration = false,
                DiscordExecutablePath = string.Empty,
                DiscordApiBaseUrl = string.Empty,
                TargetAudioEndpointId = "headset",
                RestoreAudioOnExit = true,
            },
            states: []);

        /// <summary>
        /// 기본 구성입니다. 자동 복원이 꺼져 있고 Discord 연동은 켜져 있어,
        /// 두 기능이 서로 독립임을 확인할 수 있습니다.
        /// </summary>
        public static Harness CreateWithoutRestore(params DiscordVoiceState[] states) => new(
            new AutomationSettings
            {
                WatchProcessName = "game",
                LaunchExecutablePath = "game.exe",
                UseDiscordIntegration = true,
                DiscordProcessName = "discord",
                DiscordExecutablePath = "discord.exe",
                TargetAudioEndpointId = "headset",
                DiscordApiBaseUrl = "https://example.invalid",
                RestoreAudioOnExit = false,
                DeferRestoreWhileDiscordInVoice = true,
            },
            states);
    }
}

internal sealed class FakeAudioService : IAudioEndpointService
{
    /// <summary>장치 제거 직후처럼 전환 호출 자체가 실패하는 상황을 재현합니다.</summary>
    public bool ThrowOnSet { get; set; }

    public string? DefaultId { get; set; } = "speaker";
    public IReadOnlyList<AudioEndpoint> Endpoints { get; set; } =
    [
        new AudioEndpoint("speaker", "Speakers", true),
        new AudioEndpoint("headset", "Headset", true),
        new AudioEndpoint("earbuds", "Earbuds", true),
    ];
    public List<string> SetCalls { get; } = [];

    public Task<IReadOnlyList<AudioEndpoint>> GetOutputEndpointsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Endpoints);

    public Task<string?> GetDefaultOutputIdAsync(CancellationToken cancellationToken) =>
        Task.FromResult(DefaultId);

    public Task SetDefaultOutputAsync(string endpointId, CancellationToken cancellationToken)
    {
        if (ThrowOnSet)
        {
            throw new InvalidOperationException("오디오 장치 전환 실패 테스트");
        }
        SetCalls.Add(endpointId);
        DefaultId = endpointId;
        return Task.CompletedTask;
    }
}

internal sealed class FakeProcessService : IProcessService
{
    public HashSet<string> Running { get; } = [];
    public List<string> StartedPaths { get; } = [];
    public List<string> BroughtToFront { get; } = [];

    /// <summary>실행 파일이 옮겨졌거나 UAC를 취소한 경우처럼 시작이 실패하는 상황입니다.</summary>
    public bool FailNextStart { get; set; }

    public Task<bool> IsRunningAsync(string processName, CancellationToken cancellationToken) =>
        Task.FromResult(Running.Contains(processName));

    public Task StartAsync(string executablePath, CancellationToken cancellationToken)
    {
        if (FailNextStart)
        {
            FailNextStart = false;
            throw new InvalidOperationException("실행 파일을 시작할 수 없습니다 (테스트).");
        }
        StartedPaths.Add(executablePath);
        Running.Add(Path.GetFileNameWithoutExtension(executablePath));
        return Task.CompletedTask;
    }

    public Task<bool> BringToFrontAsync(string processName, CancellationToken cancellationToken)
    {
        BroughtToFront.Add(processName);
        return Task.FromResult(true);
    }
}

internal sealed class FakeVoiceClient : IDiscordVoiceStatusClient
{
    private readonly Queue<DiscordVoiceCheck> _states;

    public FakeVoiceClient(IEnumerable<DiscordVoiceState> states)
    {
        var values = states.ToArray();
        if (values.Length == 0)
        {
            values = [DiscordVoiceState.NotInVoice];
        }
        _states = new Queue<DiscordVoiceCheck>(values.Select(state =>
            new DiscordVoiceCheck(
                state,
                state == DiscordVoiceState.Unauthorized ? "Discord API 인증 오류" : null)));
    }

    public int Calls { get; private set; }

    public Task<DiscordVoiceCheck> CheckAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_states.Count > 1 ? _states.Dequeue() : _states.Peek());
    }
}

internal sealed class FakeSessionStore : ISessionStore
{
    public AutomationSession? Session { get; set; }

    public Task<AutomationSession?> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Session);

    public Task SaveAsync(AutomationSession session, CancellationToken cancellationToken)
    {
        Session = session;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        Session = null;
        return Task.CompletedTask;
    }
}

internal sealed class FakeClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; } = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FakeLogger : IAppLogger
{
    public List<string> Messages { get; } = [];
    public void Info(string eventName, string message) => Messages.Add(message);
    public void Warning(string eventName, string message) => Messages.Add(message);
    public void Error(string eventName, Exception exception, string message) => Messages.Add(message);
}

internal sealed class FakeHotkeyService : IHotkeyService
{
    public HotkeyRegistrationResult Registration { get; set; } = new(true);
    public event Func<Task>? Pressed;
    public Task<HotkeyRegistrationResult> RegisterAsync(
        HotkeyGesture gesture,
        CancellationToken cancellationToken) => Task.FromResult(Registration);
    public void Unregister() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task RaiseAsync() => Pressed?.Invoke() ?? Task.CompletedTask;
}

internal sealed class FakeProcessMonitor : IProcessMonitor
{
    public bool Started { get; private set; }

    /// <summary>감시 대상이 이미 실행 중이어서 감시 시작과 동시에 트리거되는 상황입니다.</summary>
    public bool RaiseStartedOnStart { get; set; }

    public event Func<Task>? ProcessStarted;
    public event Func<Task>? ProcessExited;
    public async Task StartAsync(string processName, TimeSpan interval, CancellationToken cancellationToken)
    {
        Started = true;
        if (RaiseStartedOnStart)
        {
            await RaiseStartedAsync();
        }
    }
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task RaiseStartedAsync() => ProcessStarted?.Invoke() ?? Task.CompletedTask;
    public Task RaiseExitedAsync() => ProcessExited?.Invoke() ?? Task.CompletedTask;
}
