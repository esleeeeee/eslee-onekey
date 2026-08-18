using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Eslee.OneKey.Infrastructure.Windows;

/// <summary>
/// Discord가 RPC 핸드셰이크를 거부했습니다. 설정 오류(잘못된 Client ID 등)이므로
/// "Discord 미실행"과 구분해 사용자에게 그대로 알립니다.
/// </summary>
public sealed class DiscordRpcHandshakeException(string message) : Exception(message);

/// <summary>
/// Discord 로컬 RPC의 IPC 전송입니다. 공식 문서 기준으로 파이프 이름은
/// discord-ipc-0 ~ discord-ipc-9이며, 프레임은 리틀엔디언 int32 opcode +
/// int32 길이 + UTF-8 JSON 본문입니다. WebSocket 경로는 공식적으로
/// deprecated라 사용하지 않습니다.
/// </summary>
public sealed class DiscordRpcPipe : IAsyncDisposable
{
    public const int Handshake = 0;
    public const int Frame = 1;
    public const int Close = 2;

    private const int MaxPayloadBytes = 64 * 1024;

    /// <summary>파이프가 열린 채 응답하지 않는 경우를 끊는 프레임 단위 기본 시한입니다.</summary>
    public static readonly TimeSpan DefaultFrameTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// HANDSHAKE 응답 시한입니다. Discord는 연결을 수락할 준비가 되면 즉시(수백 ms)
    /// READY를 보내지만, 직전 RPC 연결을 닫은 직후에는 20초 가까이 응답하지 않습니다.
    /// 그동안은 파이프 연결만 수락되고 프레임이 오지 않으므로, 시한을 짧게 잡아
    /// 같은 재시도 예산으로 더 자주 두드려야 연결이 열리는 순간을 잡을 수 있습니다.
    /// </summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(2);

    private NamedPipeClientStream? _pipe;

    public bool IsConnected => _pipe is { IsConnected: true };

    /// <summary>
    /// discord-ipc-0부터 차례로 연결을 시도하고 HANDSHAKE까지 마칩니다.
    /// Discord가 없으면 false를 돌려주며 예외를 던지지 않습니다.
    /// </summary>
    public async Task<bool> ConnectAsync(string clientId, CancellationToken cancellationToken)
    {
        for (var index = 0; index < 10; index++)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                $"discord-ipc-{index}",
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(200, cancellationToken);
            }
            catch (Exception exception) when (exception is TimeoutException
                or IOException
                or UnauthorizedAccessException)
            {
                // 다른 사용자 세션이 쓰는 파이프일 수 있으므로 다음 인덱스를 계속 본다.
                await pipe.DisposeAsync();
                continue;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }

            _pipe = pipe;
            await SendAsync(
                Handshake,
                new { v = 1, client_id = clientId },
                cancellationToken);
            var (opcode, payload) = await ReceiveAsync(HandshakeTimeout, cancellationToken);
            using (payload)
            {
                if (opcode == Frame && payload.RootElement.TryGetProperty("evt", out var evt) &&
                    evt.GetString() == "READY")
                {
                    return true;
                }

                // CLOSE 프레임에는 사유가 담긴다(예: 4000 잘못된 Client ID).
                // 설정 오류를 "Discord 미실행"으로 오진하지 않도록 그대로 올린다.
                await DisposeAsync();
                if (opcode == Close)
                {
                    throw new DiscordRpcHandshakeException(DescribeClose(payload.RootElement));
                }
            }
        }
        return false;
    }

    private static string DescribeClose(JsonElement root)
    {
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value)
            ? value
            : 0;
        var message = root.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : null;
        return code switch
        {
            4000 => "Discord가 Client ID를 거부했습니다(4000). 애플리케이션 ID를 확인하세요.",
            4004 => "Discord RPC 버전이 맞지 않습니다(4004).",
            _ => $"Discord가 RPC 연결을 종료했습니다({code}). {message ?? "사유 없음"}",
        };
    }

    public async Task SendAsync<T>(int opcode, T payload, CancellationToken cancellationToken)
    {
        var stream = _pipe ?? throw new InvalidOperationException("RPC 파이프가 연결되지 않았습니다.");
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var buffer = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), json.Length);
        json.CopyTo(buffer.AsSpan(8));
        await stream.WriteAsync(buffer, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public Task<(int Opcode, JsonDocument Payload)> ReceiveAsync(CancellationToken cancellationToken) =>
        ReceiveAsync(DefaultFrameTimeout, cancellationToken);

    /// <summary>
    /// 프레임 하나를 읽습니다. 파이프가 열린 채 응답하지 않는 경우가 있어 반드시
    /// 시한을 겁니다. 시한이 없으면 자동화 시작이 무기한 멈출 수 있습니다.
    /// </summary>
    public async Task<(int Opcode, JsonDocument Payload)> ReceiveAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stream = _pipe ?? throw new InvalidOperationException("RPC 파이프가 연결되지 않았습니다.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var header = await ReadExactlyAsync(stream, 8, timeoutSource.Token);
            var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (length is < 0 or > MaxPayloadBytes)
            {
                throw new IOException($"RPC 프레임 길이가 올바르지 않습니다: {length}");
            }

            var body = await ReadExactlyAsync(stream, length, timeoutSource.Token);
            return (opcode, JsonDocument.Parse(Encoding.UTF8.GetString(body)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Discord RPC 응답 시간이 초과되었습니다.");
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(
        Stream stream,
        int count,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
            if (chunk == 0)
            {
                throw new IOException("RPC 파이프가 예기치 않게 닫혔습니다.");
            }
            read += chunk;
        }
        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync();
            _pipe = null;
        }
    }
}
