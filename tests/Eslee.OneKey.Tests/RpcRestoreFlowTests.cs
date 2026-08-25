using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 통화 중이면 오디오를 되돌리지 않고 기다립니다. 통화가 끝나거나 Discord 자체가
/// 꺼지면 되돌립니다. 복원 대기에는 시한이 없으므로, 판정을 못 해 영원히 기다리는
/// 상태가 생기지 않아야 합니다.
/// </summary>
public sealed class RpcRestoreFlowTests
{
    private const string DiscordProcess = "discord";

    private sealed record Harness(
        AutomationEngine Engine,
        FakeVoiceChannelClient Rpc,
        FakeAudioService Audio,
        FakeProcessService Processes);

    private static Harness Create()
    {
        var rpc = new FakeVoiceChannelClient();
        var audio = new FakeAudioService();
        var processes = new FakeProcessService();
        // Discord는 이미 떠 있다. 자동화가 굳이 실행하려 들지 않게 한다.
        processes.Running.Add(DiscordProcess);
        var settings = new AutomationSettings
        {
            WatchProcessName = "game",
            LaunchExecutablePath = "game.exe",
            TargetAudioEndpointId = "headset",
            UseDiscordIntegration = true,
            DiscordProcessName = DiscordProcess,
            DeferRestoreWhileDiscordInVoice = true,
            RestoreAudioOnExit = true,
        };
        var engine = new AutomationEngine(
            settings,
            audio,
            processes,
            new DiscordRpcVoiceStatusClient(() => rpc, processes, DiscordProcess),
            new FakeSessionStore(),
            new FakeClock(),
            new FakeLogger());
        return new Harness(engine, rpc, audio, processes);
    }

    private static async Task<Harness> StartedAsync()
    {
        var harness = Create();
        var started = await harness.Engine.StartAsync(AutomationTrigger.Hotkey);
        Assert.True(started.Started);
        Assert.Equal("headset", harness.Audio.DefaultId);
        return harness;
    }

    [Fact]
    public async Task EndingTheGameWhileInACallHoldsTheHeadset()
    {
        var harness = await StartedAsync();
        harness.Rpc.CurrentChannelId = "222222222222222222";

        await harness.Engine.OnWatchedProcessExitedAsync();

        Assert.Equal(AutomationState.RestorePending, harness.Engine.State);
        Assert.Equal("headset", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task LeavingTheCallRestoresOnTheNextCheck()
    {
        var harness = await StartedAsync();
        harness.Rpc.CurrentChannelId = "222222222222222222";
        await harness.Engine.OnWatchedProcessExitedAsync();
        Assert.Equal(AutomationState.RestorePending, harness.Engine.State);

        harness.Rpc.CurrentChannelId = null;
        await harness.Engine.ContinuePendingRestoreAsync();

        Assert.Equal(AutomationState.Completed, harness.Engine.State);
        Assert.Equal("speaker", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task ClosingDiscordWhileWaitingRestoresInsteadOfWaitingForever()
    {
        var harness = await StartedAsync();
        harness.Rpc.CurrentChannelId = "222222222222222222";
        await harness.Engine.OnWatchedProcessExitedAsync();
        Assert.Equal(AutomationState.RestorePending, harness.Engine.State);

        // 게임을 끄고 Discord까지 끄는 흔한 흐름이다. 통화 중일 수가 없으므로 되돌린다.
        harness.Processes.Running.Remove(DiscordProcess);
        await harness.Engine.ContinuePendingRestoreAsync();

        Assert.Equal(AutomationState.Completed, harness.Engine.State);
        Assert.Equal("speaker", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task EndingTheGameOutsideACallRestoresRightAway()
    {
        var harness = await StartedAsync();
        harness.Rpc.CurrentChannelId = null;

        await harness.Engine.OnWatchedProcessExitedAsync();

        Assert.Equal(AutomationState.Completed, harness.Engine.State);
        Assert.Equal("speaker", harness.Audio.DefaultId);
    }

    [Fact]
    public async Task AnRpcFailureKeepsTheHeadsetAndRetriesLater()
    {
        var harness = await StartedAsync();
        harness.Rpc.ThrowOnGetSelected = new IOException("파이프가 끊겼습니다.");

        await harness.Engine.OnWatchedProcessExitedAsync();

        Assert.Equal(AutomationState.RestorePending, harness.Engine.State);
        Assert.Equal("headset", harness.Audio.DefaultId);

        // 다음 확인에서 다시 연결해 정상 판정하면 되돌린다.
        harness.Rpc.ThrowOnGetSelected = null;
        harness.Rpc.CurrentChannelId = null;
        await harness.Engine.ContinuePendingRestoreAsync();

        Assert.Equal(AutomationState.Completed, harness.Engine.State);
        Assert.Equal("speaker", harness.Audio.DefaultId);
    }
}
