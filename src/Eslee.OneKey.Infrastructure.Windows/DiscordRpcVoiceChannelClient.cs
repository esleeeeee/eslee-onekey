using System.Text.Json;
using Eslee.OneKey.Core;

namespace Eslee.OneKey.Infrastructure.Windows;

/// <summary>
/// 공식 Discord 로컬 RPC로 음성채널을 조회·선택합니다.
/// GET_SELECTED_VOICE_CHANNEL / SELECT_VOICE_CHANNEL만 사용하며,
/// 저장된 access_token으로 AUTHENTICATE합니다. 토큰 발급은
/// <see cref="DiscordRpcAuthorizer"/>가 설정 화면에서 1회 수행합니다.
/// </summary>
public sealed class DiscordRpcVoiceChannelClient(
    string clientId,
    Func<CancellationToken, Task<string?>> accessTokenProvider,
    TimeSpan readyTimeout) : IDiscordVoiceChannelClient, IAsyncDisposable
{
    private DiscordRpcPipe? _pipe;

    /// <summary>
    /// 파이프 하나를 여러 기능이 함께 씁니다. 요청과 응답 한 쌍을 통째로 묶지 않으면
    /// 동시에 부른 쪽이 남의 응답 프레임을 읽어 버리고, 그 프레임은 nonce가 다르다는
    /// 이유로 버려집니다. 그러면 원래 주인은 응답을 영영 못 받습니다. 프레임을 읽는
    /// 도중에 겹치면 헤더와 본문이 뒤섞여 스트림 자체가 깨집니다.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 지금 파이프가 실제로 살아 있는지 여부입니다. 객체가 남아 있는 것과 연결이
    /// 살아 있는 것은 다릅니다. Discord를 다시 시작하면 파이프만 죽어 있습니다.
    /// </summary>
    public bool IsConnected => _pipe is { IsConnected: true };

    public async Task<DiscordRpcConnection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return IsConnected
                ? new DiscordRpcConnection(DiscordRpcStatus.Connected)
                : await ConnectCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await ClosePipeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DiscordRpcConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ConnectCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>호출자가 이미 게이트를 쥐고 있다고 가정합니다.</summary>
    private async Task<DiscordRpcConnection> ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new DiscordRpcConnection(
                DiscordRpcStatus.NotAuthorized,
                "Discord 애플리케이션 Client ID가 설정되지 않았습니다.");
        }

        var accessToken = await accessTokenProvider(cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new DiscordRpcConnection(
                DiscordRpcStatus.NotAuthorized,
                "Discord RPC 연결이 없습니다. 설정에서 Discord 연결을 먼저 수행하세요.");
        }

        await ClosePipeAsync();
        var deadline = DateTimeOffset.UtcNow + readyTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pipe = new DiscordRpcPipe();
            try
            {
                if (await pipe.ConnectAsync(clientId, cancellationToken))
                {
                    _pipe = pipe;
                    break;
                }
            }
            catch (DiscordRpcHandshakeException exception)
            {
                // 설정 오류다. Discord는 실행 중이므로 재시도해도 소용없다.
                await pipe.DisposeAsync();
                return new DiscordRpcConnection(DiscordRpcStatus.NotAuthorized, exception.Message);
            }
            catch (Exception exception) when (exception is IOException
                or JsonException
                or TimeoutException)
            {
                // 준비 중인 Discord는 프레임을 끝까지 주지 않을 수 있어 재시도한다.
            }

            await pipe.DisposeAsync();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new DiscordRpcConnection(
                    DiscordRpcStatus.Unavailable,
                    "Discord 클라이언트에 연결하지 못했습니다.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        var (_, authenticateError) = await SendCommandCoreAsync(
            "AUTHENTICATE",
            new { access_token = accessToken },
            cancellationToken);
        if (authenticateError is not null)
        {
            return new DiscordRpcConnection(
                DiscordRpcStatus.NotAuthorized,
                $"Discord RPC 인증에 실패했습니다({authenticateError}). 설정에서 다시 연결하세요.");
        }

        return new DiscordRpcConnection(DiscordRpcStatus.Connected);
    }

    public async Task<string?> GetSelectedVoiceChannelIdAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await GetSelectedVoiceChannelIdCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> GetSelectedVoiceChannelIdCoreAsync(CancellationToken cancellationToken)
    {
        var (data, error) = await SendCommandCoreAsync(
            "GET_SELECTED_VOICE_CHANNEL",
            new { },
            cancellationToken);
        if (error is not null)
        {
            throw new InvalidOperationException($"현재 음성채널을 확인하지 못했습니다({error}).");
        }
        if (data is null || data.Value.ValueKind != JsonValueKind.Object)
        {
            // 음성채널에 없으면 data가 null이다.
            return null;
        }
        return data.Value.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    public async Task SelectVoiceChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SelectVoiceChannelCoreAsync(channelId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SelectVoiceChannelCoreAsync(string channelId, CancellationToken cancellationToken)
    {
        var (_, error) = await SendCommandCoreAsync(
            "SELECT_VOICE_CHANNEL",
            // force=false: 이미 다른 통화 중이면 Discord가 옮기지 않는다.
            new { channel_id = channelId, force = false },
            cancellationToken);
        if (error is not null)
        {
            throw new InvalidOperationException($"Discord가 음성채널 입장을 거부했습니다({error}).");
        }
    }

    /// <summary>성공하면 (data, null), 오류 응답이면 (null, 사유)를 돌려줍니다.</summary>
    /// <summary>게이트를 쥔 상태에서만 부릅니다. 요청과 응답이 한 쌍으로 묶여야 합니다.</summary>
    private async Task<(JsonElement? Data, string? Error)> SendCommandCoreAsync<T>(
        string command,
        T args,
        CancellationToken cancellationToken)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("RPC에 연결되지 않았습니다.");
        var nonce = Guid.NewGuid().ToString();
        await pipe.SendAsync(
            DiscordRpcPipe.Frame,
            new { cmd = command, args, nonce },
            cancellationToken);

        // 같은 nonce의 응답이 올 때까지 이벤트 프레임은 건너뛴다.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var (_, document) = await pipe.ReceiveAsync(cancellationToken);
            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("nonce", out var responseNonce) ||
                    responseNonce.GetString() != nonce)
                {
                    continue;
                }
                if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
                {
                    return (null, DescribeError(root));
                }
                return (
                    root.TryGetProperty("data", out var data) ? data.Clone() : default(JsonElement),
                    null);
            }
        }
        throw new IOException("Discord RPC 응답을 받지 못했습니다.");
    }

    /// <summary>ERROR 프레임의 code/message를 진단용 문구로 만듭니다.</summary>
    private static string DescribeError(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
        {
            return "알 수 없는 오류";
        }
        var code = data.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value)
            ? value
            : 0;
        var message = data.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : null;
        return code == 0 ? message ?? "알 수 없는 오류" : $"{code}: {message ?? "사유 없음"}";
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await ClosePipeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ClosePipeAsync()
    {
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync();
            _pipe = null;
        }
    }
}
