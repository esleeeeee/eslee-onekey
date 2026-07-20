using Eslee.OneKey.Core;

namespace Eslee.OneKey.Tests;

public sealed class AutomationEngineTests
{
    [Fact]
    public async Task HotkeyStartsAppsSwitchesAudioAndRestoresWhenNotInVoice()
    {
        var harness = Harness.Create(DiscordVoiceState.NotInVoice);

        var result = await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnGameExitedAsync();

        Assert.True(result.Started);
        Assert.Equal(["game.exe", "discord.exe"], harness.Processes.StartedPaths);
        Assert.Equal(["headset", "speaker"], harness.Audio.SetCalls);
        Assert.Equal(AutomationState.Completed, harness.Engine.State);
    }

    [Fact]
    public async Task AlreadyRunningGameIsNotStartedAgain()
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
    public async Task GameExitDuringDiscordVoiceEntersRestorePending()
    {
        var harness = Harness.Create(DiscordVoiceState.InVoice);
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        await harness.Engine.OnGameExitedAsync();

        Assert.Equal(AutomationState.RestorePending, harness.Engine.State);
        Assert.Equal("headset", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task PendingRestoreCompletesAfterDiscordVoiceEnds()
    {
        var harness = Harness.Create(DiscordVoiceState.InVoice, DiscordVoiceState.NotInVoice);
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnGameExitedAsync();

        await harness.Engine.ContinuePendingRestoreAsync();

        Assert.Equal(AutomationState.Completed, harness.Engine.State);
        Assert.Equal("speaker", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task UserAudioChangeCancelsAutomaticRestore()
    {
        var harness = Harness.Create(DiscordVoiceState.InVoice, DiscordVoiceState.NotInVoice);
        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        await harness.Engine.OnGameExitedAsync();
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

        await harness.Engine.OnGameExitedAsync();

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

        await harness.Engine.OnGameExitedAsync();

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
    public async Task AlreadyRunningDiscordIsNotStartedAgain()
    {
        var harness = Harness.Create();
        harness.Processes.Running.Add("discord");

        await harness.Engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.DoesNotContain("discord.exe", harness.Processes.StartedPaths);
        Assert.Contains("discord", harness.Processes.BroughtToFront);
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

    private sealed class Harness
    {
        private Harness(params DiscordVoiceState[] states)
        {
            Settings = new AutomationSettings
            {
                GameProcessName = "game",
                GameExecutablePath = "game.exe",
                DiscordProcessName = "discord",
                DiscordExecutablePath = "discord.exe",
                TargetAudioEndpointId = "headset",
                DiscordApiBaseUrl = "https://example.invalid",
            };
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

        public static Harness Create(params DiscordVoiceState[] states) => new(states);
    }
}

internal sealed class FakeAudioService : IAudioEndpointService
{
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

    public Task<bool> IsRunningAsync(string processName, CancellationToken cancellationToken) =>
        Task.FromResult(Running.Contains(processName));

    public Task StartAsync(string executablePath, CancellationToken cancellationToken)
    {
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

    public Task<DiscordVoiceCheck> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_states.Count > 1 ? _states.Dequeue() : _states.Peek());
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
    public event Func<Task>? ProcessStarted;
    public event Func<Task>? ProcessExited;
    public Task StartAsync(string processName, TimeSpan interval, CancellationToken cancellationToken)
    {
        Started = true;
        return Task.CompletedTask;
    }
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task RaiseStartedAsync() => ProcessStarted?.Invoke() ?? Task.CompletedTask;
    public Task RaiseExitedAsync() => ProcessExited?.Invoke() ?? Task.CompletedTask;
}
