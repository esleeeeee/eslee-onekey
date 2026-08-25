using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>프로세스 조회 자체가 실패하는 상황을 재현합니다.</summary>
internal sealed class ThrowingProcessService(Exception failure) : IProcessService
{
    public Task<bool> IsRunningAsync(string processName, CancellationToken cancellationToken) =>
        Task.FromException<bool>(failure);

    public Task StartAsync(string executablePath, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<bool> BringToFrontAsync(string processName, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task StopAsync(string processName, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

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
    public async Task AFailedProcessLookupIsReportedInsteadOfThrowing()
    {
        var rpc = new FakeVoiceChannelClient();
        var processes = new ThrowingProcessService(
            new InvalidOperationException("프로세스 목록을 읽지 못했습니다."));
        var status = new DiscordRpcVoiceStatusClient(() => rpc, processes, DiscordProcess);

        var check = await status.CheckAsync(CancellationToken.None);

        // 여기서 던지면 복원 폴링 태스크가 죽어 오디오가 영영 돌아오지 않는다.
        Assert.Equal(DiscordVoiceState.Unavailable, check.State);
        Assert.Contains("프로세스 목록", check.Error);
    }

    [Fact]
    public async Task ADeadPipeIsRebuiltWithoutFailingTheCheck()
    {
        var (status, rpc, _) = Create();
        await status.CheckAsync(CancellationToken.None);
        Assert.Equal(1, rpc.ConnectAttempts);

        // Discord를 다시 시작하면 파이프가 죽는다. 실제 파이프는 입출력을 해 보기
        // 전까지 연결됐다고 답하므로, 써 보고 나서 다시 연결해야 한다.
        rpc.PipeAlive = false;
        var check = await status.CheckAsync(CancellationToken.None);

        Assert.Equal(DiscordVoiceState.NotInVoice, check.State);
        Assert.Equal(2, rpc.ConnectAttempts);
    }

    [Fact]
    public async Task AutoJoinRecoversAfterDiscordRestarts()
    {
        var (_, rpc, _) = Create();
        var join = new VoiceChannelAutoJoin(rpc, new FakeLogger());
        var settings = new AutomationSettings
        {
            UseDiscordIntegration = true,
            AutoJoinVoiceChannel = true,
            VoiceChannelTarget = "222222222222222222",
            DiscordRpcClientId = "123456789012345678",
        };
        await join.EnsureJoinedAsync(settings, CancellationToken.None);
        Assert.Equal(1, rpc.ConnectAttempts);

        // Discord를 다시 시작하면 파이프가 죽고 통화에서도 빠져 있다.
        rpc.PipeAlive = false;
        rpc.CurrentChannelId = null;
        rpc.SelectedChannels.Clear();

        // 재시작 후 첫 시도가 그대로 성공해야 한다. 실패했다가 다음 시도에 살아나는
        // 방식이면 자동화가 켜질 때마다 한 번씩 입장에 실패한다.
        var result = await join.EnsureJoinedAsync(settings, CancellationToken.None);

        Assert.Equal(2, rpc.ConnectAttempts);
        Assert.Equal(VoiceJoinOutcome.Joined, result.Outcome);
        Assert.Equal(["222222222222222222"], rpc.SelectedChannels);
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
