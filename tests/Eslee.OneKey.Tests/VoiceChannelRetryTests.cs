using Eslee.OneKey.Core;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 음성채널 입장은 Discord 재연결을 기다릴 수 있으므로 자동화 시작을 막지 않고
/// 백그라운드에서 제한된 횟수만 다시 시도해야 합니다.
/// </summary>
public sealed class VoiceChannelRetryTests
{
    private const string TargetChannelId = "222222222222222222";

    private static AutomationSettings Settings() => new()
    {
        WatchProcessName = "game",
        LaunchExecutablePath = "game.exe",
        UseDiscordIntegration = true,
        DiscordProcessName = "discord",
        DiscordExecutablePath = "discord.exe",
        TargetAudioEndpointId = "headset",
        AutoJoinVoiceChannel = true,
        VoiceChannelTarget = TargetChannelId,
        DiscordRpcClientId = "123456789012345678",
    };

    private static (AutomationEngine Engine, FakeAudioService Audio, FakeProcessService Processes)
        CreateEngine(FakeVoiceChannelClient client)
    {
        var audio = new FakeAudioService();
        var processes = new FakeProcessService();
        var logger = new FakeLogger();
        var engine = new AutomationEngine(
            Settings(),
            audio,
            processes,
            new FakeVoiceClient([]),
            new FakeSessionStore(),
            new FakeClock(),
            logger,
            new VoiceChannelAutoJoin(client, logger));
        return (engine, audio, processes);
    }

    private static readonly DiscordRpcConnection Unavailable =
        new(DiscordRpcStatus.Unavailable, "Discord 클라이언트에 연결하지 못했습니다.");

    [Fact]
    public async Task AutomationStartDoesNotWaitForTheVoiceJoin()
    {
        // Discord 재연결이 끝나지 않아도 실행 파일 시작과 오디오 전환은 끝나야 한다.
        var gate = new TaskCompletionSource();
        var client = new FakeVoiceChannelClient { ConnectGate = gate, CurrentChannelId = null };
        var (engine, audio, processes) = CreateEngine(client);

        var result = await engine.StartAsync(AutomationTrigger.Hotkey);

        Assert.True(result.Started);
        Assert.Equal(AutomationState.Active, engine.State);
        Assert.Equal("headset", audio.DefaultId);
        Assert.Contains("game.exe", processes.StartedPaths);
        Assert.Empty(client.SelectedChannels);

        gate.SetResult();
        await engine.WaitForVoiceChannelJoinAsync();
        Assert.Equal([TargetChannelId], client.SelectedChannels);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task RetriesUntilDiscordAcceptsTheReconnection()
    {
        // 실측된 실패 모양: 재연결이 몇 번 거부된 뒤 열린다.
        var client = new FakeVoiceChannelClient { CurrentChannelId = null };
        client.EnqueueConnection(Unavailable);
        client.EnqueueConnection(Unavailable);
        client.EnqueueConnection(new DiscordRpcConnection(DiscordRpcStatus.Connected));
        var (engine, _, _) = CreateEngine(client);

        await engine.StartAsync(AutomationTrigger.Hotkey);
        await engine.WaitForVoiceChannelJoinAsync();

        Assert.Equal(3, client.ConnectAttempts);
        Assert.Equal([TargetChannelId], client.SelectedChannels);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task RetryStopsAtTheAttemptLimit()
    {
        // 계속 실패해도 무한히 매달리지 않고 상한에서 멈춘다.
        var client = new FakeVoiceChannelClient { Connection = Unavailable };
        var (engine, _, _) = CreateEngine(client);

        await engine.StartAsync(AutomationTrigger.Hotkey);
        await engine.WaitForVoiceChannelJoinAsync();

        Assert.InRange(client.ConnectAttempts, 2, 5);
        Assert.Empty(client.SelectedChannels);
        Assert.NotNull(engine.LastError);
        Assert.Equal(AutomationState.Active, engine.State);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task AlreadyInTargetChannelFinishesOnTheFirstAttempt()
    {
        var client = new FakeVoiceChannelClient { CurrentChannelId = TargetChannelId };
        var (engine, _, _) = CreateEngine(client);

        await engine.StartAsync(AutomationTrigger.Hotkey);
        await engine.WaitForVoiceChannelJoinAsync();

        Assert.Equal(1, client.ConnectAttempts);
        Assert.Empty(client.SelectedChannels);
        Assert.Null(engine.LastError);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task AnotherVoiceChannelIsLeftAloneWithoutRetrying()
    {
        var client = new FakeVoiceChannelClient { CurrentChannelId = "999999999999999999" };
        var (engine, _, _) = CreateEngine(client);

        await engine.StartAsync(AutomationTrigger.Hotkey);
        await engine.WaitForVoiceChannelJoinAsync();

        Assert.Equal(1, client.ConnectAttempts);
        Assert.Empty(client.SelectedChannels);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task AuthorizationFailureIsNotRetried()
    {
        var client = new FakeVoiceChannelClient
        {
            Connection = new DiscordRpcConnection(
                DiscordRpcStatus.NotAuthorized,
                "Discord RPC 인증에 실패했습니다."),
        };
        var (engine, _, _) = CreateEngine(client);

        await engine.StartAsync(AutomationTrigger.Hotkey);
        await engine.WaitForVoiceChannelJoinAsync();

        Assert.Equal(1, client.ConnectAttempts);
        Assert.NotNull(engine.LastError);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task DisposeCancelsAPendingRetrySoNothingIsLeftRunning()
    {
        var gate = new TaskCompletionSource();
        var client = new FakeVoiceChannelClient { ConnectGate = gate, CurrentChannelId = null };
        var (engine, _, _) = CreateEngine(client);
        await engine.StartAsync(AutomationTrigger.Hotkey);

        await engine.DisposeAsync();

        Assert.Empty(client.SelectedChannels);
        Assert.True(engine.WaitForVoiceChannelJoinAsync().IsCompleted);
    }

    [Fact]
    public async Task NewSessionCancelsThePreviousRetry()
    {
        var gate = new TaskCompletionSource();
        var client = new FakeVoiceChannelClient { ConnectGate = gate, CurrentChannelId = null };
        var (engine, _, _) = CreateEngine(client);
        await engine.StartAsync(AutomationTrigger.Hotkey);
        var firstRetry = engine.WaitForVoiceChannelJoinAsync();

        // 세션을 끝내고 다시 시작하면 앞의 재시도는 남지 않아야 한다.
        await engine.KeepCurrentAndStopAsync();
        client.ConnectGate = null;
        gate.SetResult();
        await engine.StartAsync(AutomationTrigger.Hotkey);
        await engine.WaitForVoiceChannelJoinAsync();

        Assert.True(firstRetry.IsCompleted);
        Assert.Equal([TargetChannelId], client.SelectedChannels);
        await engine.DisposeAsync();
    }
}
