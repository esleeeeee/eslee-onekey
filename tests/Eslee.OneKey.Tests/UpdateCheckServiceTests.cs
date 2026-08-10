using System.Net;
using System.Net.Http;
using System.Text;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

public sealed class UpdateCheckServiceTests
{
    [Theory]
    [InlineData("v0.1.1", 0, 1, 1)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("0.2.0", 0, 2, 0)]
    [InlineData(" v2.0.1 ", 2, 0, 1)]
    public void ParsesVersionTags(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateCheckService.TryParseVersionTag(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("release-1")]
    [InlineData("v")]
    [InlineData("latest")]
    public void RejectsInvalidVersionTags(string? tag)
    {
        Assert.False(UpdateCheckService.TryParseVersionTag(tag, out _));
    }

    [Fact]
    public void ComparesVersionsIgnoringRevision()
    {
        Assert.True(UpdateCheckService.IsNewer(new Version(0, 1, 2), new Version(0, 1, 1)));
        Assert.False(UpdateCheckService.IsNewer(new Version(0, 1, 1), new Version(0, 1, 1)));
        Assert.False(UpdateCheckService.IsNewer(new Version(0, 1, 1), new Version(0, 1, 1, 0)));
        // 개발 빌드가 릴리스보다 높은 경우 업데이트를 권하지 않는다.
        Assert.False(UpdateCheckService.IsNewer(new Version(0, 1, 1), new Version(0, 2, 0)));
    }

    [Fact]
    public void FormatsVersionAsThreePartTag()
    {
        Assert.Equal("v0.1.1", UpdateCheckService.FormatVersion(new Version(0, 1, 1, 0)));
        Assert.Equal("v1.0.0", UpdateCheckService.FormatVersion(new Version(1, 0)));
    }

    [Fact]
    public async Task ReportsUpdateAvailableForNewerRelease()
    {
        var service = CreateService(
            HttpStatusCode.OK,
            """{"tag_name":"v9.9.9","html_url":"https://github.com/esleeeeee/eslee-onekey/releases/tag/v9.9.9"}""");

        var result = await service.CheckAsync(new Version(0, 1, 1), CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v9.9.9", result.LatestVersion);
        Assert.Equal(
            "https://github.com/esleeeeee/eslee-onekey/releases/tag/v9.9.9",
            result.ReleaseUrl);
    }

    [Fact]
    public async Task ReportsUpToDateForSameRelease()
    {
        var service = CreateService(HttpStatusCode.OK, """{"tag_name":"v0.1.1"}""");

        var result = await service.CheckAsync(new Version(0, 1, 1, 0), CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal(UpdateCheckService.ReleasesPageUrl, result.ReleaseUrl);
    }

    [Fact]
    public async Task ReportsNoReleaseFoundForNotFound()
    {
        // 정식 릴리스가 없거나 저장소가 비공개인 404는 네트워크 오류와 구분한다.
        var service = CreateService(HttpStatusCode.NotFound, "{}");

        var result = await service.CheckAsync(new Version(0, 1, 1), CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.NoReleaseFound, result.Status);
        Assert.Equal(UpdateCheckService.ReleasesPageUrl, result.ReleaseUrl);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "not-json")]
    [InlineData(HttpStatusCode.OK, """{"tag_name":"latest"}""")]
    [InlineData(HttpStatusCode.OK, """{"html_url":"https://example.invalid"}""")]
    public async Task ReportsFailureWithoutThrowing(HttpStatusCode statusCode, string body)
    {
        var service = CreateService(statusCode, body);

        var result = await service.CheckAsync(new Version(0, 1, 1), CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task ReportsFailureOnNetworkError()
    {
        var handler = new ThrowingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new UpdateCheckService(httpClient);

        var result = await service.CheckAsync(new Version(0, 1, 1), CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    private static UpdateCheckService CreateService(HttpStatusCode statusCode, string body)
    {
        var httpClient = new HttpClient(new StubHandler(statusCode, body));
        return new UpdateCheckService(httpClient);
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("네트워크 오류 테스트");
    }
}
