using Eslee.OneKey.Core;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 계정을 바꾸려고 사용자가 먼저 "지금 원래 장치로 복원"이나 "현재 장치 유지 후 종료"를
/// 누를 필요는 없어야 합니다. 실행 중인 자동화 환경은 그대로 두고 계정만 바꿉니다.
/// </summary>
public sealed class AccountSwitchWhileActiveTests
{
    private static readonly GameAccountProfile Korea = new()
    {
        Name = "한국 계정",
        Hotkey = new HotkeyGesture(true, true, true, false, "V"),
        SessionFilePath = @"C:\launcher\session.yaml",
    };

    private sealed record Harness(
        AutomationEngine Engine,
        FakeGameSessionService Sessions,
        FakeAudioService Audio,
        FakeProcessService Processes,
        FakeVoiceClient Voice);

    private static Harness Create()
    {
        var sessions = new FakeGameSessionService();
        var audio = new FakeAudioService();
        var processes = new FakeProcessService();
        var voice = new FakeVoiceClient([DiscordVoiceState.InVoice, DiscordVoiceState.InVoice]);
        var engine = new AutomationEngine(
            new AutomationSettings
            {
                WatchProcessName = "game",
                LaunchExecutablePath = "launcher.exe",
                TargetAudioEndpointId = "headset",
            },
            audio,
            processes,
            voice,
            new FakeSessionStore(),
            new FakeClock(),
            new FakeLogger(),
            voiceChannelAutoJoin: null,
            accountSessions: sessions);
        return new Harness(engine, sessions, audio, processes, voice);
    }

    private static async Task<Harness> CreateActiveAsync()
    {
        var harness = Create();
        var started = await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        Assert.True(started.Started);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
        return harness;
    }

    [Fact]
    public async Task AnAccountHotkeySwitchesWhileTheAutomationIsStillActive()
    {
        var harness = await CreateActiveAsync();

        var result = await harness.Engine.StartOrSwitchAccountAsync(Korea);

        Assert.True(result.Started);
        Assert.Equal([Korea.Id], harness.Sessions.Activated);
        // 실행 중인 자동화 세션을 끝내지 않는다.
        Assert.Equal(AutomationState.Active, harness.Engine.State);
    }

    [Fact]
    public async Task SwitchingAccountsNeverTouchesAudioOrVoice()
    {
        var harness = await CreateActiveAsync();
        var audioCalls = harness.Audio.SetCalls.Count;
        var voiceCalls = harness.Voice.Calls;

        await harness.Engine.StartOrSwitchAccountAsync(Korea);

        // 오디오를 원래 장치로 되돌리지 않는다.
        Assert.Equal(audioCalls, harness.Audio.SetCalls.Count);
        Assert.Equal("headset", harness.Audio.DefaultId);
        // Discord 음성채널에서 나가지도, 다시 들어가지도 않는다.
        Assert.Equal(voiceCalls, harness.Voice.Calls);
    }

    [Fact]
    public async Task SwitchingRestartsTheLauncherAndConfirmsTheLogin()
    {
        var harness = await CreateActiveAsync();
        var startedBefore = harness.Processes.StartedPaths.Count;

        await harness.Engine.StartOrSwitchAccountAsync(Korea);

        Assert.Equal(startedBefore + 1, harness.Processes.StartedPaths.Count);
        Assert.Equal("launcher.exe", harness.Processes.StartedPaths[^1]);
        Assert.Equal([Korea.Id], harness.Sessions.Confirmed);
    }

    [Fact]
    public async Task PressingTheSameAccountHotkeyAgainRestartsNothing()
    {
        var harness = await CreateActiveAsync();
        harness.Sessions.Result = new GameSessionResult(GameSessionOutcome.AlreadyActive);
        var startedBefore = harness.Processes.StartedPaths.Count;

        var result = await harness.Engine.StartOrSwitchAccountAsync(Korea);

        Assert.False(result.Started);
        Assert.Equal(startedBefore, harness.Processes.StartedPaths.Count);
        Assert.Empty(harness.Sessions.Confirmed);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
    }

    [Fact]
    public async Task ARunningGameStillBlocksTheSwitch()
    {
        var harness = await CreateActiveAsync();
        harness.Sessions.Result = new GameSessionResult(
            GameSessionOutcome.BlockedByRunningGame,
            "게임이 실행 중이라 계정을 전환하지 않았습니다.");
        var startedBefore = harness.Processes.StartedPaths.Count;

        var result = await harness.Engine.StartOrSwitchAccountAsync(Korea);

        Assert.False(result.Started);
        Assert.Contains("게임이 실행 중", harness.Engine.LastError);
        // 런처를 다시 띄우지 않고, 실행 중인 자동화도 끝내지 않는다.
        Assert.Equal(startedBefore, harness.Processes.StartedPaths.Count);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
    }

    [Fact]
    public async Task ARejectedSessionIsReportedWithoutEndingTheAutomation()
    {
        var harness = await CreateActiveAsync();
        harness.Sessions.Confirmation = new GameSessionResult(
            GameSessionOutcome.NeedsEnrollment,
            "런처가 저장된 세션을 거부했습니다.");

        var result = await harness.Engine.StartOrSwitchAccountAsync(Korea);

        Assert.False(result.Started);
        Assert.Contains("거부", harness.Engine.LastError);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
    }

    [Fact]
    public async Task WithoutARunningAutomationTheHotkeyStartsTheWholeThing()
    {
        var harness = Create();

        var result = await harness.Engine.StartOrSwitchAccountAsync(Korea);

        Assert.True(result.Started);
        Assert.Equal([Korea.Id], harness.Sessions.Activated);
        // 이때는 평소대로 오디오도 전환한다.
        Assert.Equal("headset", harness.Audio.DefaultId);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
    }
}
