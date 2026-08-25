using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Eslee.OneKey.Infrastructure.Windows;

public enum GuildIntersectionStatus
{
    /// <summary>교집합을 받았습니다. 목록이 비어 있으면 정말로 공통 서버가 없는 것입니다.</summary>
    Ok,

    /// <summary>봇이 아직 준비되지 않았습니다. 빈 목록으로 확정하면 안 됩니다.</summary>
    NotReady,

    /// <summary>토큰이 틀렸습니다.</summary>
    Unauthorized,

    /// <summary>주소가 틀렸거나 연결하지 못했습니다.</summary>
    Unavailable,
}

public sealed record GuildIntersectionResult(
    GuildIntersectionStatus Status,
    IReadOnlyList<string> GuildIds,
    string? Error = null);

/// <summary>
/// 사용자가 이미 알고 있는 서버 ID만 보내고, 그중 봇도 들어가 있는 것만 돌려받습니다.
/// 봇은 자기 서버 목록을 열거하지 않으므로 사용자가 모르던 서버가 드러나지 않고,
/// 서버 이름도 오가지 않습니다. 이름은 로컬 Discord가 이미 알고 있습니다.
/// </summary>
public sealed class DiscordGuildIntersectionClient(
    HttpClient httpClient,
    string apiBaseUrl,
    string apiToken)
{
    /// <summary>봇이 한 번에 받아 주는 최대 개수입니다.</summary>
    private const int MaxGuildIds = 200;

    public async Task<GuildIntersectionResult> IntersectAsync(
        IReadOnlyList<string> guildIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guildIds);
        if (guildIds.Count == 0)
        {
            return new GuildIntersectionResult(GuildIntersectionStatus.Ok, []);
        }

        if (!TryBuildEndpoint(apiBaseUrl, out var endpoint))
        {
            return new GuildIntersectionResult(
                GuildIntersectionStatus.Unavailable,
                [],
                "봇 API 주소가 올바른 http/https 주소가 아닙니다.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new RequestBody(guildIds.Take(MaxGuildIds).ToArray())),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new GuildIntersectionResult(
                    GuildIntersectionStatus.Unauthorized,
                    [],
                    "봇 API 인증에 실패했습니다. 고급 설정의 토큰을 확인하세요.");
            }
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return new GuildIntersectionResult(
                    GuildIntersectionStatus.NotReady,
                    [],
                    "Discord 봇이 아직 준비되지 않았습니다. 잠시 후 다시 불러오세요.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return new GuildIntersectionResult(
                    GuildIntersectionStatus.Unavailable,
                    [],
                    $"봇 API가 응답하지 않았습니다({(int)response.StatusCode}).");
            }

            var body = await response.Content.ReadFromJsonAsync<ResponseBody>(cancellationToken);
            return new GuildIntersectionResult(
                GuildIntersectionStatus.Ok,
                body?.GuildIds ?? []);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or System.Text.Json.JsonException)
        {
            return new GuildIntersectionResult(
                GuildIntersectionStatus.Unavailable,
                [],
                "봇 API에 연결하지 못했습니다. 잠시 후 다시 불러오세요.");
        }
    }

    public static bool TryBuildEndpoint(string baseUrl, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }
        endpoint = new Uri(baseUri, "/api/guild-intersection");
        return true;
    }

    private sealed record RequestBody(
        [property: JsonPropertyName("guild_ids")] IReadOnlyList<string> GuildIds);

    private sealed record ResponseBody(
        [property: JsonPropertyName("guild_ids")] IReadOnlyList<string>? GuildIds);
}
