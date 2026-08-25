using Eslee.OneKey.Core;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 단축키마다 서로 다른 계정 프로필로 자동화가 시작되는지 확인합니다.
/// </summary>
public sealed class AccountHotkeyTests
{
    private static readonly GameAccountProfile Korea = new()
    {
        Name = "한국 계정",
        Hotkey = new HotkeyGesture(true, true, true, false, "V"),
        SessionFilePath = @"C:\launcher\session.yaml",
    };

    private static readonly GameAccountProfile Asia = new()
    {
        Name = "아시아 계정",
        Hotkey = new HotkeyGesture(true, true, true, false, "A"),
        SessionFilePath = @"C:\launcher\session.yaml",
    };

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
                new AutomationCoordinator.AccountHotkey(Korea, korea),
                new AutomationCoordinator.AccountHotkey(Asia, asia),
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
            [new AutomationCoordinator.AccountHotkey(Korea, korea)]);
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
/// 자동화 기본 단축키와 계정 프로필 단축키가 겹치면 Windows가 중복 등록을 거부합니다.
/// </summary>
public sealed class AccountHotkeyConflictTests
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

    [Fact]
    public async Task TheAccountHotkeyWinsWhenItMatchesTheAutomationHotkey()
    {
        var primary = new FakeHotkeyService();
        var account = new FakeHotkeyService();
        await using var coordinator = new AutomationCoordinator(
            new AutomationSettings { WatchProcessName = "game", Hotkey = Shared },
            CreateEngine(),
            primary,
            new FakeProcessMonitor(),
            [
                new AutomationCoordinator.AccountHotkey(
                    new GameAccountProfile { Name = "한국 계정", Hotkey = Shared },
                    account),
            ]);

        await coordinator.InitializeAsync();

        Assert.False(primary.Registered);
        Assert.True(account.Registered);
        Assert.Null(coordinator.LastError);
    }

    [Fact]
    public async Task ADistinctAutomationHotkeyIsStillRegistered()
    {
        var primary = new FakeHotkeyService();
        var account = new FakeHotkeyService();
        await using var coordinator = new AutomationCoordinator(
            new AutomationSettings
            {
                WatchProcessName = "game",
                Hotkey = new HotkeyGesture(true, true, false, false, "G"),
            },
            CreateEngine(),
            primary,
            new FakeProcessMonitor(),
            [
                new AutomationCoordinator.AccountHotkey(
                    new GameAccountProfile { Name = "한국 계정", Hotkey = Shared },
                    account),
            ]);

        await coordinator.InitializeAsync();

        Assert.True(primary.Registered);
        Assert.True(account.Registered);
    }
}
