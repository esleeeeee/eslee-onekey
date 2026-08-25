using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 통화 중인지를 원격 서버가 아니라 로컬 Discord에 직접 묻습니다. 사용자 ID를
/// 넘기지 않으므로 여러 사람이 써도 각자 자기 상태만 봅니다.
/// </summary>
public sealed class DiscordRpcVoiceStatusTests
{
    private const string DiscordProcess = "discord";

    private static (DiscordRpcVoiceStatusClient Status, FakeVoiceChannelClient Rpc, FakeProcessService Processes)
        Create(bool discordRunning = true)
    {
        var rpc = new FakeVoiceChannelClient();
        var processes = new FakeProcessService();
        if (discordRunning)
        {
            processes.Running.Add(DiscordProcess);
        }
        return (new DiscordRpcVoiceStatusClient(() => rpc, processes, DiscordProcess), rpc, processes);
    }

    [Fact]
    public async Task DiscordNotRunningCountsAsNotInVoice()
    {
        var (status, rpc, _) = Create(discordRunning: false);

        var check = await status.CheckAsync(CancellationToken.None);

        Assert.Equal(DiscordVoiceState.NotInVoice, check.State);
        // Discord가 없으면 RPC를 건드릴 이유도 없다.
        Assert.Equal(0, rpc.ConnectAttempts);
    }

    [Fact]
    public async Task RunningDiscordWithoutACallIsNotInVoice()
    {
        var (status, rpc, _) = Create();
        rpc.CurrentChannelId = null;

        var check = await status.CheckAsync(CancellationToken.None);

        Assert.Equal(DiscordVoiceState.NotInVoice, check.State);
    }

    [Fact]
    public async Task ASelectedVoiceChannelMeansInVoice()
    {
        var (status, rpc, _) = Create();
        rpc.CurrentChannelId = "222222222222222222";

        var check = await status.CheckAsync(CancellationToken.None);

        Assert.Equal(DiscordVoiceState.InVoice, check.State);
    }

    [Fact]
    public async Task AnRpcFailureIsReportedAsUnavailableInsteadOfThrowing()
    {
        var (status, rpc, _) = Create();
        rpc.ThrowOnGetSelected = new InvalidOperationException("현재 음성채널을 확인하지 못했습니다.");

        var check = await status.CheckAsync(CancellationToken.None);

        // 예외가 새면 복원 폴링 태스크가 죽어 자동 복원이 영영 멈춘다.
        Assert.Equal(DiscordVoiceState.Unavailable, check.State);
        Assert.Contains("확인하지 못했습니다", check.Error);
        // 끊어 두어야 다음 확인에서 다시 연결한다.
        Assert.Equal(1, rpc.Disconnects);
    }

    [Fact]
    public async Task ARefusedConnectionIsReportedAsUnauthorized()
    {
        var (status, rpc, _) = Create();
        rpc.Connection = new DiscordRpcConnection(
            DiscordRpcStatus.NotAuthorized,
            "설정에서 Discord 연결을 먼저 수행하세요.");

        var check = await status.CheckAsync(CancellationToken.None);

        Assert.Equal(DiscordVoiceState.Unauthorized, check.State);
    }

    [Fact]
    public async Task WithoutAnRpcClientTheUserIsToldToConnect()
    {
        var processes = new FakeProcessService();
        processes.Running.Add(DiscordProcess);
        var status = new DiscordRpcVoiceStatusClient(() => null, processes, DiscordProcess);

        var check = await status.CheckAsync(CancellationToken.None);

        Assert.Equal(DiscordVoiceState.Unauthorized, check.State);
        Assert.Contains("Discord 연결", check.Error);
    }

    [Fact]
    public async Task AutoJoinAndStatusChecksShareOneConnection()
    {
        var (status, rpc, _) = Create();
        var join = new VoiceChannelAutoJoin(rpc, new FakeLogger());
        var settings = new AutomationSettings
        {
            UseDiscordIntegration = true,
            AutoJoinVoiceChannel = true,
            VoiceChannelTarget = "222222222222222222",
            DiscordRpcClientId = "123456789012345678",
        };

        await join.EnsureJoinedAsync(settings, CancellationToken.None);
        for (var i = 0; i < 5; i++)
        {
            await status.CheckAsync(CancellationToken.None);
        }

        // 5초마다 새로 연결하면 Discord가 한동안 새 연결을 거부한다.
        Assert.Equal(1, rpc.ConnectAttempts);
        Assert.Equal(0, rpc.Disconnects);
    }

    [Fact]
    public async Task AConnectionIsRebuiltAfterAFailure()
    {
        var (status, rpc, _) = Create();
        rpc.ThrowOnGetSelected = new IOException("파이프가 끊겼습니다.");
        await status.CheckAsync(CancellationToken.None);

        rpc.ThrowOnGetSelected = null;
        rpc.CurrentChannelId = null;
        var recovered = await status.CheckAsync(CancellationToken.None);

        Assert.Equal(DiscordVoiceState.NotInVoice, recovered.State);
        Assert.Equal(2, rpc.ConnectAttempts);
    }
}
