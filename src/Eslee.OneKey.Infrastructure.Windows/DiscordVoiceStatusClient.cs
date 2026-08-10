using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eslee.OneKey.Core;

namespace Eslee.OneKey.Infrastructure.Windows;

public sealed class DiscordVoiceStatusClient(
    HttpClient httpClient,
    string apiBaseUrl,
    string apiToken) : IDiscordVoiceStatusClient
{
    public async Task<DiscordVoiceCheck> CheckAsync(CancellationToken cancellationToken)
    {
        if (!TryBuildEndpoint(apiBaseUrl, out var endpoint))
        {
            return new DiscordVoiceCheck(
                DiscordVoiceState.Unavailable,
                "Discord API URL이 올바른 http/https 주소가 아닙니다.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new DiscordVoiceCheck(
                    DiscordVoiceState.Unauthorized,
                    "Discord API 인증에 실패했습니다.");
            }
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return new DiscordVoiceCheck(
                    DiscordVoiceState.NotReady,
                    "Discord bot이 아직 준비되지 않았습니다.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return new DiscordVoiceCheck(
                    DiscordVoiceState.Unavailable,
                    $"Discord API가 HTTP {(int)response.StatusCode}를 반환했습니다.");
            }

            var payload = await JsonSerializer.DeserializeAsync<VoiceStatusPayload>(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (payload?.InVoice is null)
            {
                return new DiscordVoiceCheck(
                    DiscordVoiceState.Unavailable,
                    "Discord API 응답에 in_voice 값이 없습니다.");
            }
            return new DiscordVoiceCheck(
                payload.InVoice.Value ? DiscordVoiceState.InVoice : DiscordVoiceState.NotInVoice);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DiscordVoiceCheck(DiscordVoiceState.Unavailable, "Discord API 요청 시간이 초과되었습니다.");
        }
        catch (HttpRequestException)
        {
            return new DiscordVoiceCheck(DiscordVoiceState.Unavailable, "Discord API에 연결할 수 없습니다.");
        }
        catch (JsonException)
        {
            return new DiscordVoiceCheck(DiscordVoiceState.Unavailable, "Discord API JSON 응답이 잘못되었습니다.");
        }
    }

    public static bool TryBuildEndpoint(string baseUrl, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            return false;
        }
        endpoint = new Uri(baseUri, "/api/voice-status");
        return true;
    }

    private sealed record VoiceStatusPayload(
        [property: JsonPropertyName("in_voice")] bool? InVoice);
}
