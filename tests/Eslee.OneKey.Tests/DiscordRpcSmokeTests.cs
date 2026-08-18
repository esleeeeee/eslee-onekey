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

    [DiscordRpcSmokeFact]
    public async Task RepeatedAutoJoinIsIdempotent()
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
        var join = new VoiceChannelAutoJoin(client, new FakeLogger());

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await join.EnsureJoinedAsync(settings, CancellationToken.None);
            Assert.True(
                result.IsSuccess,
                $"{attempt}회차 실패: {result.Outcome} {result.Message}");
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
