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
            new GameAccountSessionService(paths, new DpapiSecretStore(paths), processes, new FakeLogger()),
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
        await File.WriteAllTextAsync(LauncherSessionFile, "session-korea");
        await service.CaptureAsync(korea, CancellationToken.None);
        await File.WriteAllTextAsync(LauncherSessionFile, "session-asia");
        await service.CaptureAsync(asia, CancellationToken.None);

        // 아시아 계정으로 쓰는 동안 런처가 세션을 갱신했다.
        await File.WriteAllTextAsync(LauncherSessionFile, "session-asia-refreshed");
        await service.ActivateAsync(korea, CancellationToken.None);
        await service.ActivateAsync(asia, CancellationToken.None);

        Assert.Equal("session-asia-refreshed", await File.ReadAllTextAsync(LauncherSessionFile));
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
