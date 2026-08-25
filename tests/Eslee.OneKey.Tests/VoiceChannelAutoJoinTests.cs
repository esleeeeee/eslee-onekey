using Eslee.OneKey.Core;

namespace Eslee.OneKey.Tests;

public sealed class DiscordChannelTargetTests
{
    [Theory]
    [InlineData("123456789012345678", "123456789012345678")]
    [InlineData("  123456789012345678  ", "123456789012345678")]
    [InlineData("https://discord.com/channels/111111111111111111/222222222222222222", "222222222222222222")]
    [InlineData("https://discordapp.com/channels/111111111111111111/222222222222222222", "222222222222222222")]
    [InlineData("discord://-/channels/111111111111111111/222222222222222222", "222222222222222222")]
    public void ParsesLinksAndIds(string value, string expected)
    {
        Assert.True(DiscordChannelTarget.TryParse(value, out var channelId));
        Assert.Equal(expected, channelId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-link")]
    [InlineData("12345")]
    [InlineData("https://discord.com/channels/@me")]
    public void RejectsInvalidTargets(string? value)
    {
        Assert.False(DiscordChannelTarget.TryParse(value, out _));
    }
}

public sealed class VoiceChannelAutoJoinTests
{
    private const string TargetChannelId = "222222222222222222";
    private const string TargetLink = "https://discord.com/channels/111111111111111111/222222222222222222";

    private static AutomationSettings Settings(
        bool useDiscord = true,
        bool autoJoin = true,
        string target = TargetLink) => new()
    {
        UseDiscordIntegration = useDiscord,
        AutoJoinVoiceChannel = autoJoin,
        VoiceChannelTarget = target,
        DiscordRpcClientId = "123456789012345678",
    };

    private static (VoiceChannelAutoJoin Join, FakeVoiceChannelClient Client) Create(
        FakeVoiceChannelClient client) =>
        (new VoiceChannelAutoJoin(client, new FakeLogger()), client);

    [Fact]
    public async Task DisabledByDefaultDoesNothing()
    {
        var (join, client) = Create(new FakeVoiceChannelClient());

        var result = await join.EnsureJoinedAsync(new AutomationSettings(), CancellationToken.None);

        Assert.Equal(VoiceJoinOutcome.Disabled, result.Outcome);
        Assert.False(client.Connected);
        Assert.Empty(client.SelectedChannels);
    }

    [Fact]
    public async Task DiscordIntegrationOffKeepsAutoJoinOff()
    {
        var (join, client) = Create(new FakeVoiceChannelClient());

        var result = await join.EnsureJoinedAsync(
            Settings(useDiscord: false),
            CancellationToken.None);

        Assert.Equal(VoiceJoinOutcome.Disabled, result.Outcome);
        Assert.False(client.Connected);
    }

    [Fact]
    public async Task JoinsTargetChannelWhenNotInVoice()
    {
        var (join, client) = Create(new FakeVoiceChannelClient { CurrentChannelId = null });

        var result = await join.EnsureJoinedAsync(Settings(), CancellationToken.None);

        Assert.Equal(VoiceJoinOutcome.Joined, result.Outcome);
        Assert.Equal([TargetChannelId], client.SelectedChannels);
    }

    [Fact]
    public async Task DoesNothingWhenAlreadyInTargetChannel()
    {
        var (join, client) = Create(new FakeVoiceChannelClient { CurrentChannelId = TargetChannelId });

        var result = await join.EnsureJoinedAsync(Settings(), CancellationToken.None);

        Assert.Equal(VoiceJoinOutcome.AlreadyInTargetChannel, result.Outcome);
        Assert.Empty(client.SelectedChannels);
    }

    [Fact]
    public async Task DoesNotMoveUserOutOfAnotherVoiceChannel()
    {
        var (join, client) = Create(new FakeVoiceChannelClient { CurrentChannelId = "999999999999999999" });

        var result = await join.EnsureJoinedAsync(Settings(), CancellationToken.None);

        Assert.Equal(VoiceJoinOutcome.SkippedBecauseInAnotherChannel, result.Outcome);
        Assert.Empty(client.SelectedChannels);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ReportsDiscordUnavailableWhenClientIsNotRunning()
    {
        var (join, client) = Create(new FakeVoiceChannelClient
        {
            Connection = new DiscordRpcConnection(
                DiscordRpcStatus.Unavailable,
                "Discord 클라이언트에 연결하지 못했습니다."),
        });

        var result = await join.EnsureJoinedAsync(Settings(), CancellationToken.None);

        Assert.Equal(VoiceJoinOutcome.DiscordUnavailable, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(client.SelectedChannels);
    }

    [Fact]
    public async Task ReportsNotAuthorizedWhenRpcAuthenticationFails()
    {
        var (join, client) = Create(new FakeVoiceChannelClient
        {
            Connection = new DiscordRpcConnection(
                DiscordRpcStatus.NotAuthorized,
                "Discord RPC 인증에 실패했습니다."),
        });

        var result = await join.EnsureJoinedAsync(Settings(), CancellationToken.None);

        Assert.Equal(VoiceJoinOutcome.NotAuthorized, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(client.SelectedChannels);
    }

    [Fact]
    public async Task ReportsInvalidTargetWithoutTouchingDiscord()
    {
        var (join, client) = Create(new FakeVoiceChannelClient());

        var result = await join.EnsureJoinedAsync(
            Settings(target: "그냥 문자열"),
            CancellationToken.None);

        Assert.Equal(VoiceJoinOutcome.InvalidTarget, result.Outcome);
        Assert.False(client.Connected);
    }

    [Fact]
    public async Task AcceptsPlainChannelIdAsTarget()
    {
        var (join, client) = Create(new FakeVoiceChannelClient { CurrentChannelId = null });

        var result = await join.EnsureJoinedAsync(
            Settings(target: TargetChannelId),
            CancellationToken.None);

        Assert.Equal(VoiceJoinOutcome.Joined, result.Outcome);
        Assert.Equal([TargetChannelId], client.SelectedChannels);
    }
}

/// <summary>
/// 자동 입장 실패가 실행 파일 시작·오디오 전환을 실패시키지 않는지 확인합니다.
/// </summary>
public sealed class VoiceChannelAutoJoinIsolationTests
{
    private static (AutomationEngine Engine, FakeAudioService Audio, FakeProcessService Processes)
        CreateEngine(FakeVoiceChannelClient voiceChannelClient)
    {
        var settings = new AutomationSettings
        {
            WatchProcessName = "game",
            LaunchExecutablePath = "game.exe",
            UseDiscordIntegration = true,
            DiscordProcessName = "discord",
            DiscordExecutablePath = "discord.exe",
            TargetAudioEndpointId = "headset",
            AutoJoinVoiceChannel = true,
            VoiceChannelTarget = "222222222222222222",
            DiscordRpcClientId = "123456789012345678",
        };
        var audio = new FakeAudioService();
        var processes = new FakeProcessService();
        var logger = new FakeLogger();
        var engine = new AutomationEngine(
            settings,
            audio,
            processes,
            new FakeVoiceClient([]),
            new FakeSessionStore(),
            new FakeClock(),
            logger,
            new VoiceChannelAutoJoin(voiceChannelClient, logger));
        return (engine, audio, processes);
    }

    [Fact]
    public async Task AutomationStartsEvenWhenDiscordIsNotRunning()
    {
        var (engine, audio, processes) = CreateEngine(new FakeVoiceChannelClient
        {
            Connection = new DiscordRpcConnection(DiscordRpcStatus.Unavailable, "Discord 없음"),
        });

        var result = await engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.True(result.Started);
        Assert.Equal(AutomationState.Active, engine.State);
        Assert.Equal("headset", audio.DefaultId);
        Assert.Contains("game.exe", processes.StartedPaths);
        Assert.NotNull(engine.LastError);
    }

    [Fact]
    public async Task AutomationStartsEvenWhenVoiceJoinThrows()
    {
        var (engine, audio, processes) = CreateEngine(new FakeVoiceChannelClient
        {
            CurrentChannelId = null,
            ThrowOnSelect = true,
        });

        var result = await engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.True(result.Started);
        Assert.Equal(AutomationState.Active, engine.State);
        Assert.Equal("headset", audio.DefaultId);
        Assert.Contains("game.exe", processes.StartedPaths);
    }

    [Fact]
    public async Task SuccessfulJoinLeavesNoError()
    {
        var (engine, audio, _) = CreateEngine(new FakeVoiceChannelClient { CurrentChannelId = null });

        var result = await engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.True(result.Started);
        Assert.Null(engine.LastError);
        Assert.Equal("headset", audio.DefaultId);
    }
}

public sealed class DiscordRpcTokenTests
{
    [Fact]
    public void TokensWithoutExpiryAreNotRefreshed()
    {
        Assert.False(Eslee.OneKey.Infrastructure.Windows.DiscordRpcAuthorizer.NeedsRefresh(
            new Eslee.OneKey.Infrastructure.Windows.DiscordRpcTokens("token")));
    }

    [Fact]
    public void TokensNearExpiryAreRefreshed()
    {
        var tokens = new Eslee.OneKey.Infrastructure.Windows.DiscordRpcTokens(
            "token",
            "refresh",
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.True(Eslee.OneKey.Infrastructure.Windows.DiscordRpcAuthorizer.NeedsRefresh(tokens));
    }

    [Fact]
    public void FreshTokensAreNotRefreshed()
    {
        var tokens = new Eslee.OneKey.Infrastructure.Windows.DiscordRpcTokens(
            "token",
            "refresh",
            DateTimeOffset.UtcNow.AddDays(6));

        Assert.False(Eslee.OneKey.Infrastructure.Windows.DiscordRpcAuthorizer.NeedsRefresh(tokens));
    }

    [Fact]
    public void PkceChallengeIsDeterministicBase64Url()
    {
        var verifier = Eslee.OneKey.Infrastructure.Windows.DiscordRpcAuthorizer.CreateCodeVerifier();
        var challenge = Eslee.OneKey.Infrastructure.Windows.DiscordRpcAuthorizer
            .CreateCodeChallenge(verifier);

        // Discord는 43자 이상의 verifier와 S256 challenge를 요구한다.
        Assert.True(verifier.Length >= 43);
        Assert.Equal(
            challenge,
            Eslee.OneKey.Infrastructure.Windows.DiscordRpcAuthorizer.CreateCodeChallenge(verifier));
        Assert.DoesNotContain('+', challenge);
        Assert.DoesNotContain('/', challenge);
        Assert.DoesNotContain('=', challenge);
    }
}

internal sealed class FakeVoiceChannelClient : IDiscordVoiceChannelClient
{
    private readonly Queue<DiscordRpcConnection> _connectionResults = new();

    public DiscordRpcConnection Connection { get; set; } = new(DiscordRpcStatus.Connected);
    public string? CurrentChannelId { get; set; }
    public bool Connected { get; private set; }
    public bool ThrowOnSelect { get; set; }
    public List<string> SelectedChannels { get; } = [];

    /// <summary>연결 시도 횟수입니다. 재시도가 몇 번 일어났는지 확인합니다.</summary>
    public int ConnectAttempts { get; private set; }

    /// <summary>설정하면 ConnectAsync가 이 신호를 기다립니다(비차단 검증용).</summary>
    public TaskCompletionSource? ConnectGate { get; set; }

    /// <summary>앞에서부터 차례로 돌려줄 연결 결과입니다. 비면 Connection을 씁니다.</summary>
    public void EnqueueConnection(DiscordRpcConnection connection) =>
        _connectionResults.Enqueue(connection);

    public async Task<DiscordRpcConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectAttempts++;
        if (ConnectGate is not null)
        {
            await ConnectGate.Task.WaitAsync(cancellationToken);
        }
        var connection = _connectionResults.Count > 0 ? _connectionResults.Dequeue() : Connection;
        Connected = connection.Status == DiscordRpcStatus.Connected;
        PipeAlive = true;
        return connection;
    }

    /// <summary>
    /// 파이프가 살아 있는지입니다. 객체가 남아 있는 것과 연결이 살아 있는 것은 다릅니다.
    /// Discord를 다시 시작하면 객체는 그대로인데 파이프만 죽습니다.
    /// </summary>
    public bool PipeAlive { get; set; } = true;

    public async Task<DiscordRpcConnection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        // 실제 파이프는 상대가 죽어도 입출력을 해 보기 전까지 연결됐다고 답한다.
        // 그래서 여기서 PipeAlive를 보지 않는다. 회복은 명령이 실패할 때 일어난다.
        if (Connected)
        {
            return new DiscordRpcConnection(DiscordRpcStatus.Connected);
        }
        return await ConnectAsync(cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        Connected = false;
        Disconnects++;
        return Task.CompletedTask;
    }

    /// <summary>연결을 끊은 횟수입니다. 오류 후 재연결을 확인합니다.</summary>
    public int Disconnects { get; private set; }

    /// <summary>설정하면 조회가 예외를 던집니다(RPC 오류 재현용).</summary>
    public Exception? ThrowOnGetSelected { get; set; }

    public async Task<string?> GetSelectedVoiceChannelIdAsync(CancellationToken cancellationToken)
    {
        if (ThrowOnGetSelected is not null)
        {
            throw ThrowOnGetSelected;
        }
        if (!PipeAlive)
        {
            // 실제 클라이언트는 깨진 것을 확인하면 버리고 한 번 다시 연결해 이어 간다.
            Connected = false;
            await ConnectAsync(cancellationToken);
        }
        return CurrentChannelId;
    }

    public Task SelectVoiceChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        if (ThrowOnSelect)
        {
            throw new InvalidOperationException("Discord가 음성채널 입장을 거부했습니다 (테스트).");
        }
        SelectedChannels.Add(channelId);
        CurrentChannelId = channelId;
        return Task.CompletedTask;
    }
}
