using Eslee.OneKey.Core;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 단축키는 자동화 규칙에만 있습니다. 규칙마다 다른 계정 프로필로 시작하는지 봅니다.
/// </summary>
public sealed class AutomationRuleHotkeyTests
{
    private static readonly GameAccountProfile Korea = new()
    {
        Name = "한국 계정",
        SessionFilePath = @"C:\launcher\session.yaml",
    };

    private static readonly GameAccountProfile Asia = new()
    {
        Name = "아시아 계정",
        SessionFilePath = @"C:\launcher\session.yaml",
    };

    private static AutomationSettings Rule(string name, string key, Guid profileId) => new()
    {
        Name = name,
        Hotkey = new HotkeyGesture(true, true, true, false, key),
        AccountProfileId = profileId,
        WatchProcessName = "game",
        LaunchExecutablePath = "game.exe",
        TargetAudioEndpointId = "headset",
    };

    private static readonly AutomationSettings KoreaRule = Rule("한국", "V", Korea.Id);
    private static readonly AutomationSettings AsiaRule = Rule("아시아", "A", Asia.Id);

    private static (AutomationEngine Engine, FakeGameSessionService Sessions) CreateEngine()
    {
        var sessions = new FakeGameSessionService();
        var logger = new FakeLogger();
        var engine = new AutomationEngine(
            new AutomationSettings
            {
                WatchProcessName = "game",
                LaunchExecutablePath = "game.exe",
                TargetAudioEndpointId = "headset",
            },
            new FakeAudioService(),
            new FakeProcessService(),
            new FakeVoiceClient([]),
            new FakeSessionStore(),
            new FakeClock(),
            logger,
            voiceChannelAutoJoin: null,
            accountSessions: sessions);
        return (engine, sessions);
    }

    [Fact]
    public async Task EachAccountHotkeyStartsWithItsOwnProfile()
    {
        var (engine, sessions) = CreateEngine();
        var korea = new FakeHotkeyService();
        var asia = new FakeHotkeyService();
        await using var coordinator = new AutomationCoordinator(
            new AutomationSettings { WatchProcessName = "game", Hotkey = new HotkeyGesture() },
            engine,
            new FakeHotkeyService(),
            new FakeProcessMonitor(),
            [
                new AutomationCoordinator.AutomationRuleBinding(KoreaRule, korea, Korea),
                new AutomationCoordinator.AutomationRuleBinding(AsiaRule, asia, Asia),
            ]);
        await coordinator.InitializeAsync();

        await korea.RaiseAsync();
        Assert.Equal([Korea.Id], sessions.Activated);

        // 첫 자동화를 끝내고 다른 단축키를 누르면 다른 계정이 선택돼야 한다.
        await engine.KeepCurrentAndStopAsync();
        await asia.RaiseAsync();

        Assert.Equal([Korea.Id, Asia.Id], sessions.Activated);
    }

    [Fact]
    public async Task PausedAutomationIgnoresAccountHotkeys()
    {
        var (engine, sessions) = CreateEngine();
        var korea = new FakeHotkeyService();
        await using var coordinator = new AutomationCoordinator(
            new AutomationSettings { WatchProcessName = "game", Hotkey = new HotkeyGesture() },
            engine,
            new FakeHotkeyService(),
            new FakeProcessMonitor(),
            [new AutomationCoordinator.AutomationRuleBinding(KoreaRule, korea, Korea)]);
        await coordinator.InitializeAsync();
        coordinator.SetPaused(true);

        await korea.RaiseAsync();

        Assert.Empty(sessions.Activated);
    }

    [Fact]
    public async Task AFailedAccountSwitchStopsTheAutomationBeforeItChangesAnything()
    {
        var (engine, sessions) = CreateEngine();
        sessions.Result = new GameSessionResult(
            GameSessionOutcome.NeedsEnrollment,
            "이 계정의 로그인 세션이 저장되지 않았습니다.");

        var result = await engine.StartAsync(AutomationTrigger.Hotkey, Korea);

        Assert.False(result.Started);
        Assert.Equal(AutomationState.Failed, engine.State);
        Assert.Contains("저장되지 않았습니다", engine.LastError);
    }

    [Fact]
    public async Task WithoutAProfileTheAutomationNeverTouchesAccounts()
    {
        var (engine, sessions) = CreateEngine();

        var result = await engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.True(result.Started);
        Assert.Empty(sessions.Activated);
    }
}

internal sealed class FakeGameSessionService : IGameSessionService
{
    public List<Guid> Activated { get; } = [];
    public List<Guid> Confirmed { get; } = [];
    public GameSessionResult Result { get; set; } = new(GameSessionOutcome.Switched);
    public GameSessionResult Confirmation { get; set; } = new(GameSessionOutcome.Switched);

    public Task<GameSessionResult> ConfirmActiveAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        Confirmed.Add(profile.Id);
        return Task.FromResult(Confirmation);
    }

    public Task<bool> CaptureAsync(GameAccountProfile profile, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    /// <summary>런처 실행 파일이 사라진 경우처럼 예외가 터지는 상황을 만듭니다.</summary>
    public Exception? Throws { get; set; }

    public Task<GameSessionResult> ActivateAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        Activated.Add(profile.Id);
        return Throws is null ? Task.FromResult(Result) : Task.FromException<GameSessionResult>(Throws);
    }

    public Task<bool> HasStoredSessionAsync(Guid profileId, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public Task<GameAccountProfileStatus> GetStatusAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken) =>
        Task.FromResult(GameAccountProfileStatus.Enrolled);

    public Task<GameSessionResult> PrepareForNewSignInAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken) =>
        Task.FromResult(new GameSessionResult(GameSessionOutcome.Switched));

    public Task ForgetAsync(Guid profileId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// 단축키는 규칙에만 있으므로 같은 조합이 두 번 등록될 일이 없어야 합니다.
/// 두 규칙이 같은 조합을 쓰면 Windows가 거부하기 전에 우리가 먼저 걸러 알립니다.
/// </summary>
public sealed class AutomationRuleHotkeyConflictTests
{
    private static readonly HotkeyGesture Shared = new(true, true, true, false, "V");

    private static AutomationEngine CreateEngine() => new(
        new AutomationSettings { WatchProcessName = "game", TargetAudioEndpointId = "headset" },
        new FakeAudioService(),
        new FakeProcessService(),
        new FakeVoiceClient([]),
        new FakeSessionStore(),
        new FakeClock(),
        new FakeLogger());

    private static AutomationSettings Rule(string name, HotkeyGesture hotkey) => new()
    {
        Name = name,
        Hotkey = hotkey,
        WatchProcessName = "game",
    };

    [Fact]
    public async Task TwoRulesCannotClaimTheSameHotkey()
    {
        var first = new FakeHotkeyService();
        var second = new FakeHotkeyService();
        await using var coordinator = new AutomationCoordinator(
            new AutomationSettings { WatchProcessName = "game" },
            CreateEngine(),
            new FakeHotkeyService(),
            new FakeProcessMonitor(),
            [
                new AutomationCoordinator.AutomationRuleBinding(Rule("한국", Shared), first),
                new AutomationCoordinator.AutomationRuleBinding(Rule("아시아", Shared), second),
            ]);

        await coordinator.InitializeAsync();

        Assert.True(first.Registered);
        Assert.False(second.Registered);
        Assert.Contains("같은 단축키", coordinator.LastError);
    }

    [Fact]
    public async Task DistinctRuleHotkeysAreAllRegistered()
    {
        var first = new FakeHotkeyService();
        var second = new FakeHotkeyService();
        await using var coordinator = new AutomationCoordinator(
            new AutomationSettings { WatchProcessName = "game" },
            CreateEngine(),
            new FakeHotkeyService(),
            new FakeProcessMonitor(),
            [
                new AutomationCoordinator.AutomationRuleBinding(Rule("한국", Shared), first),
                new AutomationCoordinator.AutomationRuleBinding(
                    Rule("아시아", new HotkeyGesture(true, true, true, false, "A")), second),
            ]);

        await coordinator.InitializeAsync();

        Assert.True(first.Registered);
        Assert.True(second.Registered);
        Assert.Null(coordinator.LastError);
    }

    [Fact]
    public async Task TheStandaloneHotkeyIsUsedOnlyWhenThereAreNoRules()
    {
        var standalone = new FakeHotkeyService();
        await using var coordinator = new AutomationCoordinator(
            new AutomationSettings { WatchProcessName = "game", Hotkey = Shared },
            CreateEngine(),
            standalone,
            new FakeProcessMonitor());

        await coordinator.InitializeAsync();

        Assert.True(standalone.Registered);
    }
}
