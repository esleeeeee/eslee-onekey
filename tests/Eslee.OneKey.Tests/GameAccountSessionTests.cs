using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 계정 전환은 런처 세션 파일을 프로필별로 바꿔 넣는 방식입니다. 비밀번호를 다루지
/// 않으므로 여기서 검증할 것은 파일 왕복과 "이미 그 계정이면 건드리지 않기"입니다.
/// </summary>
public sealed class GameAccountSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "onekey-tests",
        Guid.NewGuid().ToString("N"));

    private string LauncherSessionFile => Path.Combine(_root, "launcher", "session.yaml");

    private (GameAccountSessionService Service, FakeProcessService Processes) CreateService()
    {
        var paths = new ApplicationPaths(Path.Combine(_root, "onekey"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(Path.GetDirectoryName(LauncherSessionFile)!);
        var processes = new FakeProcessService();
        return (
            new GameAccountSessionService(
                paths,
                new DpapiSecretStore(paths),
                processes,
                new FakeLogger(),
                // 테스트에서는 런처를 기다리며 실제로 잠들 이유가 없다.
                confirmPollInterval: TimeSpan.FromMilliseconds(1)),
            processes);
    }

    private GameAccountProfile Profile(string name) => new()
    {
        Name = name,
        SessionFilePath = LauncherSessionFile,
        LauncherProcessNames = ["launcher"],
        BlockingProcessNames = ["game"],
    };

    [Fact]
    public async Task CaptureThenActivateRestoresTheSameSession()
    {
        var (service, _) = CreateService();
        var korea = Profile("한국 계정");
        await File.WriteAllTextAsync(LauncherSessionFile, "session-korea");

        Assert.True(await service.CaptureAsync(korea, CancellationToken.None));
        await File.WriteAllTextAsync(LauncherSessionFile, "something-else");
        var result = await service.ActivateAsync(korea, CancellationToken.None);

        Assert.Equal(GameSessionOutcome.Switched, result.Outcome);
        Assert.Equal("session-korea", await File.ReadAllTextAsync(LauncherSessionFile));
    }

    [Fact]
    public async Task SwitchingBetweenTwoAccountsIsDeterministic()
    {
        var (service, _) = CreateService();
        var korea = Profile("한국 계정");
        var asia = Profile("아시아 계정");

        await File.WriteAllTextAsync(LauncherSessionFile, "session-korea");
        await service.CaptureAsync(korea, CancellationToken.None);
        await File.WriteAllTextAsync(LauncherSessionFile, "session-asia");
        await service.CaptureAsync(asia, CancellationToken.None);

        // A -> B -> A 전환이 매번 올바른 세션을 남긴다.
        Assert.Equal(GameSessionOutcome.Switched, (await service.ActivateAsync(korea, CancellationToken.None)).Outcome);
        Assert.Equal("session-korea", await File.ReadAllTextAsync(LauncherSessionFile));

        Assert.Equal(GameSessionOutcome.Switched, (await service.ActivateAsync(asia, CancellationToken.None)).Outcome);
        Assert.Equal("session-asia", await File.ReadAllTextAsync(LauncherSessionFile));

        Assert.Equal(GameSessionOutcome.Switched, (await service.ActivateAsync(korea, CancellationToken.None)).Outcome);
        Assert.Equal("session-korea", await File.ReadAllTextAsync(LauncherSessionFile));
    }

    [Fact]
    public async Task AlreadyActiveAccountIsNotSwitchedAgain()
    {
        var (service, processes) = CreateService();
        var korea = Profile("한국 계정");
        await File.WriteAllTextAsync(LauncherSessionFile, "session-korea");
        await service.CaptureAsync(korea, CancellationToken.None);

        var result = await service.ActivateAsync(korea, CancellationToken.None);

        Assert.Equal(GameSessionOutcome.AlreadyActive, result.Outcome);
        // 런처를 닫지도 않는다 = 불필요한 재로그인이 없다.
        Assert.Empty(processes.Stopped);
    }

    [Fact]
    public async Task SwitchingClosesTheLauncherFirst()
    {
        var (service, processes) = CreateService();
        var korea = Profile("한국 계정");
        var asia = Profile("아시아 계정");
        await File.WriteAllTextAsync(LauncherSessionFile, "session-korea");
        await service.CaptureAsync(korea, CancellationToken.None);
        await File.WriteAllTextAsync(LauncherSessionFile, "session-asia");
        await service.CaptureAsync(asia, CancellationToken.None);
        processes.Running.Add("launcher");

        await service.ActivateAsync(korea, CancellationToken.None);

        Assert.Contains("launcher", processes.Stopped);
    }

    [Fact]
    public async Task RunningGameBlocksTheSwitch()
    {
        var (service, processes) = CreateService();
        var korea = Profile("한국 계정");
        var asia = Profile("아시아 계정");
        await File.WriteAllTextAsync(LauncherSessionFile, "session-korea");
        await service.CaptureAsync(korea, CancellationToken.None);
        await File.WriteAllTextAsync(LauncherSessionFile, "session-asia");
        await service.CaptureAsync(asia, CancellationToken.None);
        processes.Running.Add("game");

        var result = await service.ActivateAsync(korea, CancellationToken.None);

        Assert.Equal(GameSessionOutcome.BlockedByRunningGame, result.Outcome);
        Assert.Equal("session-asia", await File.ReadAllTextAsync(LauncherSessionFile));
    }

    [Fact]
    public async Task ProfileWithoutAStoredSessionAsksForEnrollment()
    {
        var (service, _) = CreateService();

        var result = await service.ActivateAsync(Profile("아시아 계정"), CancellationToken.None);

        Assert.Equal(GameSessionOutcome.NeedsEnrollment, result.Outcome);
        Assert.False(result.CanContinue);
    }

    [Fact]
    public async Task RefreshedSessionIsCapturedBackBeforeSwitchingAway()
    {
        var (service, _) = CreateService();
        var korea = Profile("한국 계정");
        var asia = Profile("아시아 계정");
        // 실제 세션 파일에는 로그인 유지 토큰이 들어 있다. 그게 있어야 로그인된
        // 세션과 런처가 비워 버린 세션을 구분할 수 있다.
        await File.WriteAllTextAsync(LauncherSessionFile, "refresh_token: korea");
        await service.CaptureAsync(korea, CancellationToken.None);
        await File.WriteAllTextAsync(LauncherSessionFile, "refresh_token: asia");
        await service.CaptureAsync(asia, CancellationToken.None);

        // 아시아 계정으로 쓰는 동안 런처가 세션을 갱신했다.
        await File.WriteAllTextAsync(LauncherSessionFile, "refresh_token: asia-rotated");
        await service.ActivateAsync(korea, CancellationToken.None);
        await service.ActivateAsync(asia, CancellationToken.None);

        Assert.Equal("refresh_token: asia-rotated", await File.ReadAllTextAsync(LauncherSessionFile));
    }

    [Fact]
    public async Task StoredSessionIsNotReadableAsPlainText()
    {
        var (service, _) = CreateService();
        var korea = Profile("한국 계정");
        await File.WriteAllTextAsync(LauncherSessionFile, "session-korea-secret-cookie");
        await service.CaptureAsync(korea, CancellationToken.None);

        var stored = Directory
            .EnumerateFiles(Path.Combine(_root, "onekey"), "*", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith("session.yaml", StringComparison.Ordinal))
            .Select(File.ReadAllBytes);

        Assert.All(stored, bytes =>
            Assert.DoesNotContain(
                "session-korea-secret-cookie",
                System.Text.Encoding.UTF8.GetString(bytes)));
    }

    [Fact]
    public async Task ReactivatingTheSameAccountKeepsTheRotatedToken()
    {
        var (service, processes) = CreateService();
        var korea = Profile("한국 계정");
        await File.WriteAllTextAsync(LauncherSessionFile, "refresh_token: korea-v1");
        await service.CaptureAsync(korea, CancellationToken.None);

        // 런처가 로그인하면서 토큰을 회전시켰다.
        await File.WriteAllTextAsync(LauncherSessionFile, "refresh_token: korea-v2");
        var again = await service.ActivateAsync(korea, CancellationToken.None);

        Assert.Equal(GameSessionOutcome.AlreadyActive, again.Outcome);
        // 같은 계정이므로 런처를 닫지 않는다.
        Assert.Empty(processes.Stopped);

        // 회전본이 저장됐는지는 다른 내용으로 덮은 뒤 되돌려 확인한다.
        await File.WriteAllTextAsync(LauncherSessionFile, "signed-out");
        await service.ActivateAsync(korea, CancellationToken.None);

        Assert.Equal("refresh_token: korea-v2", await File.ReadAllTextAsync(LauncherSessionFile));
    }

    [Fact]
    public async Task ASignedOutFileIsNeverStoredOverAGoodSession()
    {
        var (service, _) = CreateService();
        var korea = Profile("한국 계정");
        var asia = Profile("아시아 계정");
        await File.WriteAllTextAsync(LauncherSessionFile, "refresh_token: korea");
        await service.CaptureAsync(korea, CancellationToken.None);
        await File.WriteAllTextAsync(LauncherSessionFile, "refresh_token: asia");
        await service.CaptureAsync(asia, CancellationToken.None);
        await service.ActivateAsync(korea, CancellationToken.None);

        // 런처가 세션을 비워 버린 상태에서 다른 계정으로 전환한다.
        await File.WriteAllTextAsync(LauncherSessionFile, "no-token-here");
        await service.ActivateAsync(asia, CancellationToken.None);
        await service.ActivateAsync(korea, CancellationToken.None);

        Assert.Equal("refresh_token: korea", await File.ReadAllTextAsync(LauncherSessionFile));
    }

    [Fact]
    public async Task LoginIsConfirmedWhenTheLauncherRotatesTheToken()
    {
        var (service, processes) = CreateService();
        var korea = Profile("한국 계정");
        await File.WriteAllTextAsync(LauncherSessionFile, "refresh_token: korea-v1");
        await service.CaptureAsync(korea, CancellationToken.None);
        processes.Running.Add("launcher");

        // 런처가 뜨면서 토큰을 회전시킨 상태를 만든다.
        await File.WriteAllTextAsync(LauncherSessionFile, "refresh_token: korea-v2");
        var result = await service.ConfirmActiveAsync(korea, CancellationToken.None);

        Assert.Equal(GameSessionOutcome.Switched, result.Outcome);
        Assert.Equal(GameAccountProfileStatus.Enrolled, await service.GetStatusAsync(korea, CancellationToken.None));

        // 확인 과정에서 회전본을 되받아 둔다.
        await File.WriteAllTextAsync(LauncherSessionFile, "signed-out");
        await service.ActivateAsync(korea, CancellationToken.None);
        Assert.Equal("refresh_token: korea-v2", await File.ReadAllTextAsync(LauncherSessionFile));
    }

    [Fact]
    public async Task ARejectedSessionAsksForReenrollment()
    {
        var (service, processes) = CreateService();
        var korea = Profile("한국 계정");
        await File.WriteAllTextAsync(LauncherSessionFile, "refresh_token: korea");
        await service.CaptureAsync(korea, CancellationToken.None);
        processes.Running.Add("launcher");

        // 런처가 저장본을 거부하면 로그인 유지 토큰을 지운다.
        await File.WriteAllTextAsync(LauncherSessionFile, "signed-out");
        var result = await service.ConfirmActiveAsync(korea, CancellationToken.None);

        Assert.Equal(GameSessionOutcome.NeedsEnrollment, result.Outcome);
        Assert.Equal(
            GameAccountProfileStatus.NeedsReenrollment,
            await service.GetStatusAsync(korea, CancellationToken.None));
    }

    [Fact]
    public async Task AMissingLauncherLeavesTheProfileAlone()
    {
        var (service, _) = CreateService();
        var korea = Profile("한국 계정");
        await File.WriteAllTextAsync(LauncherSessionFile, "refresh_token: korea");
        await service.CaptureAsync(korea, CancellationToken.None);

        // 런처가 뜨지 않으면 판정하지 않는다. 멀쩡한 프로필을 재등록 필요로 몰지 않는다.
        var result = await service.ConfirmActiveAsync(korea, CancellationToken.None);

        Assert.Equal(GameSessionOutcome.Switched, result.Outcome);
        Assert.Equal(GameAccountProfileStatus.Enrolled, await service.GetStatusAsync(korea, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
