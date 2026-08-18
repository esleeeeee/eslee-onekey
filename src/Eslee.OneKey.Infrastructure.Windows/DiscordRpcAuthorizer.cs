using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eslee.OneKey.Infrastructure.Windows;

public sealed record DiscordRpcTokens(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken = null,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null,
    [property: JsonPropertyName("client_secret")] string? ClientSecret = null);

public sealed record DiscordRpcAuthorizationResult(bool Succeeded, string Message);

/// <summary>
/// 공식 RPC 인증을 1회 수행합니다. 흐름은 Discord 공식 문서 그대로입니다:
/// IPC HANDSHAKE → AUTHORIZE(사용자가 Discord 창에서 승인) → code →
/// POST /oauth2/token → access_token.
///
/// 토큰 교환은 client_secret 없이 PKCE로 먼저 시도합니다(개발자 포털의
/// Public Client 필요). Discord가 이를 거부하면, 사용자가 자신의 앱에서 발급해
/// 직접 입력한 client secret으로만 교환합니다. 어느 경우에도 secret과 토큰은
/// DPAPI 파일에만 기록하며 settings.json·소스·로그에 남기지 않습니다.
/// </summary>
public sealed class DiscordRpcAuthorizer(HttpClient httpClient)
{
    /// <summary>개발자 포털 OAuth2 탭에 이 값을 Redirect URI로 등록해야 합니다.</summary>
    public const string RedirectUri = "http://localhost/onekey";

    private const string TokenEndpoint = "https://discord.com/api/oauth2/token";

    /// <summary>사용자가 Discord 승인 창을 처리할 때까지 기다리는 최대 시간입니다.</summary>
    private static readonly TimeSpan UserApprovalTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 만료가 임박한 access_token을 refresh_token으로 갱신합니다. 갱신할 수 없으면
    /// null을 돌려주고, 호출부는 기존 토큰을 그대로 쓰다가 실패 시 재연결을 안내합니다.
    /// </summary>
    public async Task<DiscordRpcTokens?> RefreshAsync(
        string clientId,
        DiscordRpcTokens tokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken) || string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var fields = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken,
        };
        if (!string.IsNullOrWhiteSpace(tokens.ClientSecret))
        {
            fields["client_secret"] = tokens.ClientSecret;
        }

        var (refreshed, _) = await PostTokenAsync(fields, cancellationToken);
        return refreshed is null ? null : refreshed with { ClientSecret = tokens.ClientSecret };
    }

    /// <summary>만료 5분 전부터 갱신 대상으로 봅니다.</summary>
    public static bool NeedsRefresh(DiscordRpcTokens tokens) =>
        tokens.ExpiresAt is not null &&
        tokens.ExpiresAt.Value - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(5);

    public async Task<(DiscordRpcAuthorizationResult Result, DiscordRpcTokens? Tokens)> AuthorizeAsync(
        string clientId,
        string? clientSecret,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return (new DiscordRpcAuthorizationResult(false, "Client ID를 입력하세요."), null);
        }

        var verifier = CreateCodeVerifier();
        await using var pipe = new DiscordRpcPipe();
        try
        {
            if (!await pipe.ConnectAsync(clientId, cancellationToken))
            {
                return (
                    new DiscordRpcAuthorizationResult(
                        false,
                        "Discord 클라이언트를 찾지 못했습니다. Discord를 실행한 뒤 다시 시도하세요."),
                    null);
            }
        }
        catch (Exception exception) when (exception is DiscordRpcHandshakeException
            or TimeoutException
            or IOException)
        {
            return (new DiscordRpcAuthorizationResult(false, exception.Message), null);
        }

        var nonce = Guid.NewGuid().ToString();
        await pipe.SendAsync(
            DiscordRpcPipe.Frame,
            new
            {
                cmd = "AUTHORIZE",
                args = new
                {
                    client_id = clientId,
                    scopes = new[] { "rpc" },
                    code_challenge = CreateCodeChallenge(verifier),
                    code_challenge_method = "S256",
                },
                nonce,
            },
            cancellationToken);

        string? code = null;
        for (var attempt = 0; attempt < 20 && code is null; attempt++)
        {
            // 사용자가 Discord 창에서 승인 버튼을 눌러야 응답이 오므로 넉넉히 기다리되,
            // 창을 무시한 경우 영구히 멈추지 않도록 시한을 둔다.
            (int _, JsonDocument document) response;
            try
            {
                response = await pipe.ReceiveAsync(UserApprovalTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return (
                    new DiscordRpcAuthorizationResult(
                        false,
                        "Discord 승인 창에서 응답이 없었습니다. 다시 시도하세요."),
                    null);
            }

            var (_, document) = response;
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
                    var message = root.TryGetProperty("data", out var errorData) &&
                        errorData.TryGetProperty("message", out var text)
                        ? text.GetString()
                        : null;
                    return (
                        new DiscordRpcAuthorizationResult(
                            false,
                            $"Discord가 승인을 거부했습니다: {message ?? "알 수 없는 오류"}. " +
                            "앱 소유자 계정인지, 테스터 목록에 계정이 있는지 확인하세요."),
                        null);
                }
                if (root.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("code", out var codeElement))
                {
                    code = codeElement.GetString();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return (
                new DiscordRpcAuthorizationResult(false, "Discord 승인 응답을 받지 못했습니다."),
                null);
        }

        var (pkceTokens, pkceError) = await ExchangeAsync(clientId, null, code, verifier, cancellationToken);
        if (pkceTokens is not null)
        {
            return (
                new DiscordRpcAuthorizationResult(true, "Discord RPC에 연결했습니다. (PKCE, secret 미사용)"),
                pkceTokens);
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            return (
                new DiscordRpcAuthorizationResult(
                    false,
                    $"secret 없는 PKCE 교환이 거부되었습니다({pkceError}). " +
                    "개발자 포털 OAuth2 탭에서 Public Client를 켜거나, " +
                    "본인 앱의 Client Secret을 입력해 다시 시도하세요."),
                null);
        }

        var (secretTokens, secretError) = await ExchangeAsync(
            clientId,
            clientSecret,
            code,
            verifier,
            cancellationToken);
        return secretTokens is not null
            ? (
                new DiscordRpcAuthorizationResult(true, "Discord RPC에 연결했습니다."),
                secretTokens with { ClientSecret = clientSecret })
            : (
                new DiscordRpcAuthorizationResult(false, $"토큰 교환에 실패했습니다: {secretError}"),
                null);
    }

    private async Task<(DiscordRpcTokens? Tokens, string? Error)> ExchangeAsync(
        string clientId,
        string? clientSecret,
        string code,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = codeVerifier,
        };
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            fields["client_secret"] = clientSecret;
        }

        return await PostTokenAsync(fields, cancellationToken);
    }

    private async Task<(DiscordRpcTokens? Tokens, string? Error)> PostTokenAsync(
        Dictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(fields);
            using var response = await httpClient.PostAsync(TokenEndpoint, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // 본문에 secret이 포함될 일은 없지만 오류 코드만 노출한다.
                using var errorDocument = JsonDocument.Parse(body);
                var error = errorDocument.RootElement.TryGetProperty("error", out var value)
                    ? value.GetString()
                    : $"HTTP {(int)response.StatusCode}";
                return (null, error);
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var accessToken = root.TryGetProperty("access_token", out var token) ? token.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return (null, "응답에 access_token이 없습니다.");
            }

            var expiresIn = root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 0;
            return (
                new DiscordRpcTokens(
                    accessToken,
                    root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
                    expiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresIn) : null),
                null);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return (null, "Discord 토큰 엔드포인트에 연결하지 못했습니다.");
        }
    }

    /// <summary>PKCE code_verifier입니다. Discord는 43자 이상을 요구합니다.</summary>
    public static string CreateCodeVerifier() =>
        Base64Url(RandomNumberGenerator.GetBytes(64));

    public static string CreateCodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
