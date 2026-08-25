using Eslee.OneKey.Core;

namespace Eslee.OneKey.Infrastructure.Windows;

/// <summary>
/// 통화 중인지를 로컬 Discord에 직접 물어봅니다. 원격 서버에 물어보던 방식과 달리
/// 사용자 ID를 어디에도 넘기지 않습니다. 로컬 RPC는 곧 그 PC에 로그인한 본인이라,
/// 여러 사람이 써도 각자 자기 상태만 보게 됩니다.
///
/// 음성채널 자동 입장과 같은 RPC 연결을 함께 씁니다. Discord는 연결을 끊은 직후
/// 한동안 새 연결을 거부하므로, 주기적인 조회가 매번 다시 연결하면 안 됩니다.
/// 연결은 실행 중에 다시 만들어질 수 있어 인스턴스가 아니라 공급자로 받습니다.
/// </summary>
public sealed class DiscordRpcVoiceStatusClient(
    Func<IDiscordVoiceChannelClient?> clientProvider,
    IProcessService processes,
    string discordProcessName) : IDiscordVoiceStatusClient
{
    public async Task<DiscordVoiceCheck> CheckAsync(CancellationToken cancellationToken)
    {
        IDiscordVoiceChannelClient? client = null;
        try
        {
            // Discord가 없으면 통화 중일 수 없다. 여기서 Unavailable을 돌려주면 복원이
            // 영원히 보류된다. 복원 대기에는 시한이 없고 사용자가 직접 눌러야만 끝난다.
            // 프로세스 조회 자체도 실패할 수 있어 try 안에서 부른다.
            if (string.IsNullOrWhiteSpace(discordProcessName) ||
                !await processes.IsRunningAsync(discordProcessName, cancellationToken))
            {
                return new DiscordVoiceCheck(DiscordVoiceState.NotInVoice);
            }

            client = clientProvider();
            if (client is null)
            {
                return new DiscordVoiceCheck(
                    DiscordVoiceState.Unauthorized,
                    "Discord 연결이 설정되지 않아 통화 상태를 확인할 수 없습니다. " +
                    "설정에서 Discord 연결을 먼저 수행하세요.");
            }

            var connection = await client.EnsureConnectedAsync(cancellationToken);
            if (connection.Status != DiscordRpcStatus.Connected)
            {
                return new DiscordVoiceCheck(
                    connection.Status == DiscordRpcStatus.NotAuthorized
                        ? DiscordVoiceState.Unauthorized
                        : DiscordVoiceState.Unavailable,
                    connection.Error);
            }

            var channelId = await client.GetSelectedVoiceChannelIdAsync(cancellationToken);
            return new DiscordVoiceCheck(
                string.IsNullOrWhiteSpace(channelId)
                    ? DiscordVoiceState.NotInVoice
                    : DiscordVoiceState.InVoice);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 예외가 그대로 새면 복원 폴링 태스크가 죽어 자동 복원이 영영 멈춘다.
            // 끊어 두면 다음 확인에서 다시 연결한다.
            if (client is not null)
            {
                await SafeDisconnectAsync(client, cancellationToken);
            }
            return new DiscordVoiceCheck(DiscordVoiceState.Unavailable, exception.Message);
        }
    }

    private static async Task SafeDisconnectAsync(
        IDiscordVoiceChannelClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.DisconnectAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 이미 끊긴 연결을 닫는 중일 뿐이다. 여기서 실패해도 알릴 것이 없다.
        }
    }
}
