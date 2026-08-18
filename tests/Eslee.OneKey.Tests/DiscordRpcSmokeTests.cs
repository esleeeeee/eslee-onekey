using System.Text.Json;
using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 실제 Discord 클라이언트와 저장된 RPC 인증이 있는 PC에서만 의미가 있는 smoke test.
/// 연속 재연결에서 간헐적으로 실패하던 문제를 실물로 확인합니다.
/// 대상 채널을 바꾸지 않으며(이미 그 채널이면 무동작), 강제 이동도 하지 않습니다.
/// ONEKEY_DISCORD_RPC_SMOKE=1 일 때만 실행됩니다.
/// </summary>
public sealed class DiscordRpcSmokeTests
{
    [DiscordRpcSmokeFact]
    public async Task ConsecutiveReconnectsAllSucceed()
    {
        var paths = new ApplicationPaths();
        var settings = (await new JsonSettingsStore(paths).LoadAsync(CancellationToken.None))
            .Automations.FirstOrDefault();
        Assert.NotNull(settings);
        Assert.False(string.IsNullOrWhiteSpace(settings.DiscordRpcClientId));

        var secrets = new DpapiSecretStore(paths);
        await using var client = new DiscordRpcVoiceChannelClient(
            settings.DiscordRpcClientId,
            async cancellationToken => JsonSerializer.Deserialize<DiscordRpcTokens>(
                await secrets.LoadRpcSecretsAsync(cancellationToken) ?? "")?.AccessToken,
            TimeSpan.FromSeconds(30));

        // 같은 클라이언트로 연속 연결 — 예전에는 2회차에서 20초를 소진하고 실패했다.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var connection = await client.ConnectAsync(CancellationToken.None);
            Assert.Equal(DiscordRpcStatus.Connected, connection.Status);
            await client.GetSelectedVoiceChannelIdAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 반복 호출이 무동작인지 확인합니다. 이 테스트는 사용자를 음성채널로
    /// 끌어들이면 안 되므로, 이미 대상 채널에 있을 때만 검사하고 그렇지 않으면
    /// 아무것도 하지 않습니다. 실제 입장 경로는 단위 테스트가 덮습니다.
    /// </summary>
    [DiscordRpcSmokeFact]
    public async Task RepeatedAutoJoinIsIdempotentWhenAlreadyInTheTargetChannel()
    {
        var paths = new ApplicationPaths();
        var settings = (await new JsonSettingsStore(paths).LoadAsync(CancellationToken.None))
            .Automations.First();
        var secrets = new DpapiSecretStore(paths);
        await using var client = new DiscordRpcVoiceChannelClient(
            settings.DiscordRpcClientId,
            async cancellationToken => JsonSerializer.Deserialize<DiscordRpcTokens>(
                await secrets.LoadRpcSecretsAsync(cancellationToken) ?? "")?.AccessToken,
            TimeSpan.FromSeconds(30));

        var connection = await client.ConnectAsync(CancellationToken.None);
        if (connection.Status != DiscordRpcStatus.Connected)
        {
            return;
        }

        var current = await client.GetSelectedVoiceChannelIdAsync(CancellationToken.None);
        DiscordChannelTarget.TryParse(settings.VoiceChannelTarget, out var target);
        if (current is null || current != target)
        {
            // 대상 채널 밖이라면 여기서 멈춘다. 시험 때문에 사용자를 입장시키지 않는다.
            return;
        }

        var join = new VoiceChannelAutoJoin(client, new FakeLogger());
        for (var round = 1; round <= 3; round++)
        {
            var result = await join.EnsureJoinedAsync(settings, CancellationToken.None);
            Assert.Equal(VoiceJoinOutcome.AlreadyInTargetChannel, result.Outcome);
        }
    }
}

public sealed class DiscordRpcSmokeFactAttribute : FactAttribute
{
    public DiscordRpcSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ONEKEY_DISCORD_RPC_SMOKE") != "1")
        {
            Skip = "실제 Discord RPC smoke test는 ONEKEY_DISCORD_RPC_SMOKE=1 설정 시에만 실행됩니다.";
        }
    }
}
