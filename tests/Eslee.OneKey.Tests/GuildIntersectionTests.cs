using System.Net;
using System.Text;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 서버 목록은 로컬 Discord가 알려 주고, 봇은 그중 자기도 들어가 있는 것만 확인해 줍니다.
/// 봇이 아직 준비되지 않은 것과 정말로 공통 서버가 없는 것은 구분해야 합니다. 준비 중을
/// "서버 없음"으로 확정하면 사용자는 고를 것이 없다고 오해합니다.
/// </summary>
public sealed class GuildIntersectionTests
{
    private const string BaseUrl = "https://bot.example";
    private static readonly string[] Requested = ["1048000000000000002", "1271000000000000004"];

    private static DiscordGuildIntersectionClient Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler), BaseUrl, "token");

    [Fact]
    public async Task OnlyTheServersTheBotAlsoJoinedComeBack()
    {
        var client = Create(new CapturingHandler(
            HttpStatusCode.OK,
            """{"guild_ids":["1048000000000000002"]}"""));

        var result = await client.IntersectAsync(Requested, CancellationToken.None);

        Assert.Equal(GuildIntersectionStatus.Ok, result.Status);
        Assert.Equal(["1048000000000000002"], result.GuildIds);
    }

    [Fact]
    public async Task TheRequestSendsOnlyTheIdsWeAlreadyKnow()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"guild_ids":[]}""");
        var client = Create(handler);

        await client.IntersectAsync(Requested, CancellationToken.None);

        Assert.Equal("/api/guild-intersection", handler.Path);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("1048000000000000002", handler.Body);
        Assert.Contains("guild_ids", handler.Body);
        Assert.Equal("Bearer token", handler.Authorization);
    }

    [Fact]
    public async Task ABotThatIsNotReadyIsNotTheSameAsNoServers()
    {
        var client = Create(new CapturingHandler(
            HttpStatusCode.ServiceUnavailable,
            """{"error":"discord_not_ready"}"""));

        var result = await client.IntersectAsync(Requested, CancellationToken.None);

        Assert.Equal(GuildIntersectionStatus.NotReady, result.Status);
        Assert.Contains("다시 불러오", result.Error);
    }

    [Fact]
    public async Task ARefusedTokenIsReported()
    {
        var client = Create(new CapturingHandler(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}"""));

        var result = await client.IntersectAsync(Requested, CancellationToken.None);

        Assert.Equal(GuildIntersectionStatus.Unauthorized, result.Status);
    }

    [Fact]
    public async Task AnUnreachableBotIsReportedInsteadOfThrowing()
    {
        var client = Create(new ThrowingHttpHandler());

        var result = await client.IntersectAsync(Requested, CancellationToken.None);

        Assert.Equal(GuildIntersectionStatus.Unavailable, result.Status);
        Assert.Contains("연결하지 못했", result.Error);
    }

    [Fact]
    public async Task AnEmptyLocalListNeverCallsTheBot()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"guild_ids":[]}""");
        var client = Create(handler);

        var result = await client.IntersectAsync([], CancellationToken.None);

        Assert.Equal(GuildIntersectionStatus.Ok, result.Status);
        Assert.Empty(result.GuildIds);
        Assert.Null(handler.Path);
    }

    [Fact]
    public void ABadBaseUrlIsRejectedBeforeAnyRequest()
    {
        Assert.False(DiscordGuildIntersectionClient.TryBuildEndpoint("not-a-url", out _));
        Assert.True(DiscordGuildIntersectionClient.TryBuildEndpoint(BaseUrl, out var endpoint));
        Assert.Equal("/api/guild-intersection", endpoint!.AbsolutePath);
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Method = request.Method;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("연결할 수 없습니다."));
    }
}
