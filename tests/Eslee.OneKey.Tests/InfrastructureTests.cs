using System.Net;
using System.Net.Http;
using System.Text;
using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

public sealed class InfrastructureTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"in_voice\":true}", DiscordVoiceState.InVoice)]
    [InlineData(HttpStatusCode.OK, "{\"in_voice\":false}", DiscordVoiceState.NotInVoice)]
    [InlineData(HttpStatusCode.Unauthorized, "{}", DiscordVoiceState.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, "{}", DiscordVoiceState.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", DiscordVoiceState.NotReady)]
    [InlineData(HttpStatusCode.OK, "{}", DiscordVoiceState.Unavailable)]
    [InlineData(HttpStatusCode.OK, "not-json", DiscordVoiceState.Unavailable)]
    public async Task DiscordClientMapsContractAndFailures(
        HttpStatusCode code,
        string body,
        DiscordVoiceState expected)
    {
        var handler = new StubHttpHandler(code, body);
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        var client = new DiscordVoiceStatusClient(
            httpClient,
            "https://api.example.test/base",
            "secure-test-token");

        var result = await client.CheckAsync(CancellationToken.None);

        Assert.Equal(expected, result.State);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secure-test-token", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task DpapiSecretStoreRoundTripsWithoutPlaintextFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "onekey-tests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        var store = new DpapiSecretStore(paths);
        const string token = "secure-random-test-token";
        try
        {
            await store.SaveDiscordApiTokenAsync(token, CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(paths.SecretFile);
            var loaded = await store.LoadDiscordApiTokenAsync(CancellationToken.None);

            Assert.Equal(token, loaded);
            Assert.DoesNotContain(token, Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task JsonSettingsNeverContainApiToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "onekey-tests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        var store = new JsonSettingsStore(paths);
        try
        {
            await store.SaveAsync(new AppSettings(), CancellationToken.None);
            var json = await File.ReadAllTextAsync(paths.SettingsFile);

            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, (await store.LoadAsync(CancellationToken.None)).SchemaVersion);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LoggerRedactsBearerAndAuthorizationValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "onekey-tests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        try
        {
            var logger = new FileAppLogger(paths);
            logger.Warning("test", "Authorization: Bearer should-not-leak");

            var log = File.ReadAllText(paths.LogFile);
            Assert.DoesNotContain("should-not-leak", log);
            Assert.Contains("[REDACTED]", log);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class StubHttpHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
