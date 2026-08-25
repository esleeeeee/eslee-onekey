using Eslee.OneKey.Core;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 규칙이 여럿일 때의 동작입니다. 같은 실행 환경에 계정만 다른 규칙이면 오디오와
/// 통화를 그대로 둔 채 계정만 바꿔야 합니다.
/// </summary>
public sealed class AutomationRuleTests
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

    private static AutomationSettings Rule(string name, string key, Guid? profileId) => new()
    {
        Name = name,
        Hotkey = new HotkeyGesture(true, true, true, false, key),
        AccountProfileId = profileId,
        WatchProcessName = "game",
        LaunchExecutablePath = "launcher.exe",
        TargetAudioEndpointId = "headset",
    };

    private sealed record Harness(
        AutomationEngine Engine,
        FakeGameSessionService Sessions,
        FakeAudioService Audio,
        FakeProcessService Processes,
        FakeVoiceClient Voice);

    private static Harness Create(AutomationSettings initial)
    {
        var sessions = new FakeGameSessionService();
        var audio = new FakeAudioService();
        var processes = new FakeProcessService();
        var voice = new FakeVoiceClient([DiscordVoiceState.InVoice, DiscordVoiceState.InVoice]);
        var engine = new AutomationEngine(
            initial, audio, processes, voice, new FakeSessionStore(),
            new FakeClock(), new FakeLogger(), voiceChannelAutoJoin: null, accountSessions: sessions);
        return new Harness(engine, sessions, audio, processes, voice);
    }

    [Fact]
    public async Task ARuleStartsTheAutomationWithItsOwnAccount()
    {
        var korea = Rule("한국", "V", Korea.Id);
        var harness = Create(korea);

        var result = await harness.Engine.StartRuleAsync(korea, Korea);

        Assert.True(result.Started);
        Assert.Equal([Korea.Id], harness.Sessions.Activated);
        Assert.Equal("headset", harness.Audio.DefaultId);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
        Assert.Equal(korea, harness.Engine.ActiveRule);
    }

    [Fact]
    public async Task AnotherRuleWithTheSameEnvironmentOnlySwitchesTheAccount()
    {
        var korea = Rule("한국", "V", Korea.Id);
        var asia = Rule("아시아", "A", Asia.Id);
        var harness = Create(korea);
        await harness.Engine.StartRuleAsync(korea, Korea);
        var audioCalls = harness.Audio.SetCalls.Count;
        var voiceCalls = harness.Voice.Calls;

        var result = await harness.Engine.StartRuleAsync(asia, Asia);

        Assert.True(result.Started);
        Assert.Equal([Korea.Id, Asia.Id], harness.Sessions.Activated);
        // 오디오를 되돌리지도, 통화를 다시 잡지도 않는다.
        Assert.Equal(audioCalls, harness.Audio.SetCalls.Count);
        Assert.Equal("headset", harness.Audio.DefaultId);
        Assert.Equal(voiceCalls, harness.Voice.Calls);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
        Assert.Equal(asia, harness.Engine.ActiveRule);
    }

    [Fact]
    public async Task ARuleWithADifferentEnvironmentIsIgnoredWhileAnotherIsRunning()
    {
        var korea = Rule("한국", "V", Korea.Id);
        var other = Rule("다른 게임", "B", Asia.Id) with
        {
            LaunchExecutablePath = "other.exe",
            TargetAudioEndpointId = "speaker",
        };
        var harness = Create(korea);
        await harness.Engine.StartRuleAsync(korea, Korea);

        var result = await harness.Engine.StartRuleAsync(other, Asia);

        Assert.False(result.Started);
        Assert.Equal([Korea.Id], harness.Sessions.Activated);
        // 돌고 있는 자동화를 건드리지 않는다.
        Assert.Equal(korea, harness.Engine.ActiveRule);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
    }

    [Fact]
    public async Task PressingTheSameRuleAgainDoesNotRestartTheLauncher()
    {
        var korea = Rule("한국", "V", Korea.Id);
        var harness = Create(korea);
        await harness.Engine.StartRuleAsync(korea, Korea);
        harness.Sessions.Result = new GameSessionResult(GameSessionOutcome.AlreadyActive);
        var started = harness.Processes.StartedPaths.Count;

        var result = await harness.Engine.StartRuleAsync(korea, Korea);

        Assert.False(result.Started);
        Assert.Equal(started, harness.Processes.StartedPaths.Count);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
    }

    [Fact]
    public async Task ARuleWithoutAnAccountNeverTouchesAccounts()
    {
        var plain = Rule("계정 없음", "G", profileId: null);
        var harness = Create(plain);

        var result = await harness.Engine.StartRuleAsync(plain, accountProfile: null);

        Assert.True(result.Started);
        Assert.Empty(harness.Sessions.Activated);
        Assert.Equal("headset", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task ARunningGameStillBlocksARuleSwitch()
    {
        var korea = Rule("한국", "V", Korea.Id);
        var asia = Rule("아시아", "A", Asia.Id);
        var harness = Create(korea);
        await harness.Engine.StartRuleAsync(korea, Korea);
        harness.Sessions.Result = new GameSessionResult(
            GameSessionOutcome.BlockedByRunningGame,
            "게임이 실행 중이라 계정을 전환하지 않았습니다.");

        var result = await harness.Engine.StartRuleAsync(asia, Asia);

        Assert.False(result.Started);
        Assert.Contains("게임이 실행 중", harness.Engine.LastError);
        Assert.Equal(AutomationState.Active, harness.Engine.State);
    }
}
