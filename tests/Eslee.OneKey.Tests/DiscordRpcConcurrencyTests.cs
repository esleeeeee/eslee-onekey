using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// RPC 파이프 하나를 자동 입장과 통화 상태 조회가 함께 씁니다. 요청과 응답을 한 쌍으로
/// 묶지 않으면 동시에 부른 쪽이 남의 응답 프레임을 읽고, 그 프레임은 nonce가 다르다는
/// 이유로 버려집니다. 원래 주인은 응답을 영영 못 받고, 읽는 도중에 겹치면 헤더와 본문이
/// 뒤섞여 스트림 자체가 깨집니다.
///
/// 파이프 없이 확인하기 위해 토큰 공급자를 관찰 지점으로 씁니다. 토큰이 비면 연결은
/// 파이프를 만들기 전에 끝나므로, 공급자가 겹쳐 불리는지만 보면 직렬화 여부를 알 수
/// 있습니다.
/// </summary>
public sealed class DiscordRpcConcurrencyTests
{
    private const string ClientId = "123456789012345678";

    [Fact]
    public async Task ConcurrentCallsNeverOverlapInsideTheClient()
    {
        var running = 0;
        var peak = 0;
        var lockObject = new object();

        var client = new DiscordRpcVoiceChannelClient(
            ClientId,
            async _ =>
            {
                lock (lockObject)
                {
                    running++;
                    peak = Math.Max(peak, running);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                lock (lockObject)
                {
                    running--;
                }
                // 토큰이 비면 파이프를 만들기 전에 끝난다.
                return null;
            },
            TimeSpan.FromSeconds(1));

        var first = client.ConnectAsync(CancellationToken.None);
        var second = client.ConnectAsync(CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.Equal(1, peak);
        Assert.Equal(DiscordRpcStatus.NotAuthorized, (await first).Status);
        Assert.Equal(DiscordRpcStatus.NotAuthorized, (await second).Status);
    }

    [Fact]
    public async Task DisconnectWaitsForAnInFlightConnect()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new DiscordRpcVoiceChannelClient(
            ClientId,
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return null;
            },
            TimeSpan.FromSeconds(1));

        var connecting = client.ConnectAsync(CancellationToken.None);
        await entered.Task;

        var disconnecting = client.DisconnectAsync(CancellationToken.None);
        // 연결이 진행 중인 동안에는 끊기가 끼어들면 안 된다.
        Assert.False(disconnecting.IsCompleted);

        release.SetResult();
        await connecting;
        await disconnecting;

        Assert.True(disconnecting.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task EnsureConnectedAlsoTakesTheSameGate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var client = new DiscordRpcVoiceChannelClient(
            ClientId,
            async _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return null;
            },
            TimeSpan.FromSeconds(1));

        var connecting = client.ConnectAsync(CancellationToken.None);
        await entered.Task;

        var ensuring = client.EnsureConnectedAsync(CancellationToken.None);
        Assert.False(ensuring.IsCompleted);

        release.SetResult();
        await connecting;
        await ensuring;

        Assert.Equal(2, calls);
    }
}
