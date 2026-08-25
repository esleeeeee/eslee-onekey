using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 등록 상태 표시와 "로그아웃 없이 다음 계정 로그인 준비" 동작을 검증합니다.
/// 실측 결과 런처의 로그아웃은 서버에서 refresh token을 폐기해 이미 등록해 둔
/// 다른 계정의 저장본까지 무효로 만들기 때문에 그 경로는 쓰지 않습니다.
/// </summary>
public sealed class GameAccountEnrollmentTests : IDisposable
{
    private const string SignedIn = "psl:\n  authorization:\n    client:\n      refresh_token: TOKEN\n";
    private const string SignedOut = "psl:\n  authorization: null\n";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "onekey-tests",
        Guid.NewGuid().ToString("N"));

    private string SessionFile => Path.Combine(_root, "launcher", "session.yaml");

    private (GameAccountSessionService Service, FakeProcessService Processes) Create()
    {
        var paths = new ApplicationPaths(Path.Combine(_root, "onekey"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(Path.GetDirectoryName(SessionFile)!);
        var processes = new FakeProcessService();
        return (
            new GameAccountSessionService(paths, new DpapiSecretStore(paths), processes, new FakeLogger()),
            processes);
    }

    private GameAccountProfile Profile(string name) => new()
    {
        Name = name,
        SessionFilePath = SessionFile,
        LauncherProcessNames = ["launcher"],
        BlockingProcessNames = ["game"],
    };

    [Fact]
    public async Task ProfileWithoutASessionIsNotEnrolled()
    {
        var (service, _) = Create();

        Assert.Equal(
            GameAccountProfileStatus.NotEnrolled,
            await service.GetStatusAsync(Profile("한국"), CancellationToken.None));
    }

    [Fact]
    public async Task CapturedProfileIsEnrolled()
    {
        var (service, _) = Create();
        var korea = Profile("한국");
        await File.WriteAllTextAsync(SessionFile, SignedIn);
        await service.CaptureAsync(korea, CancellationToken.None);

        Assert.Equal(
            GameAccountProfileStatus.Enrolled,
            await service.GetStatusAsync(korea, CancellationToken.None));
    }

    [Fact]
    public async Task ARejectedSessionIsReportedAsNeedingReenrollment()
    {
        var (service, _) = Create();
        var korea = Profile("한국");
        await File.WriteAllTextAsync(SessionFile, SignedIn);
        await service.CaptureAsync(korea, CancellationToken.None);

        // 런처가 저장된 토큰을 거부하면 세션 파일에서 토큰을 지운다.
        await File.WriteAllTextAsync(SessionFile, SignedOut);

        Assert.Equal(
            GameAccountProfileStatus.NeedsReenrollment,
            await service.GetStatusAsync(korea, CancellationToken.None));
    }

    [Fact]
    public async Task ReenrollingClearsTheWarning()
    {
        var (service, _) = Create();
        var korea = Profile("한국");
        await File.WriteAllTextAsync(SessionFile, SignedIn);
        await service.CaptureAsync(korea, CancellationToken.None);
        await File.WriteAllTextAsync(SessionFile, SignedOut);
        Assert.Equal(
            GameAccountProfileStatus.NeedsReenrollment,
            await service.GetStatusAsync(korea, CancellationToken.None));

        await File.WriteAllTextAsync(SessionFile, SignedIn + "# new");
        await service.CaptureAsync(korea, CancellationToken.None);

        Assert.Equal(
            GameAccountProfileStatus.Enrolled,
            await service.GetStatusAsync(korea, CancellationToken.None));
    }

    [Fact]
    public async Task PreparingForANewSignInKeepsTheCurrentAccountAndClearsTheSession()
    {
        var (service, processes) = Create();
        var korea = Profile("한국");
        await File.WriteAllTextAsync(SessionFile, SignedIn);
        await service.CaptureAsync(korea, CancellationToken.None);

        // 로그인 유지 상태가 갱신된 뒤 다음 계정을 등록하려 한다.
        await File.WriteAllTextAsync(SessionFile, SignedIn + "# refreshed");
        processes.Running.Add("launcher");
        var result = await service.PrepareForNewSignInAsync(korea, CancellationToken.None);

        Assert.Equal(GameSessionOutcome.Switched, result.Outcome);
        Assert.False(File.Exists(SessionFile));
        Assert.Contains("launcher", processes.Stopped);

        // 준비 과정에서 한국 계정의 최신 세션이 사라지면 안 된다.
        await service.ActivateAsync(korea, CancellationToken.None);
        Assert.Equal(SignedIn + "# refreshed", await File.ReadAllTextAsync(SessionFile));
    }

    [Fact]
    public async Task PreparingForANewSignInIsRefusedWhileTheGameRuns()
    {
        var (service, processes) = Create();
        await File.WriteAllTextAsync(SessionFile, SignedIn);
        processes.Running.Add("game");

        var result = await service.PrepareForNewSignInAsync(Profile("한국"), CancellationToken.None);

        Assert.Equal(GameSessionOutcome.BlockedByRunningGame, result.Outcome);
        Assert.True(File.Exists(SessionFile));
    }

    [Fact]
    public async Task ForgettingAProfileRemovesItsStoredSession()
    {
        var (service, _) = Create();
        var korea = Profile("한국");
        await File.WriteAllTextAsync(SessionFile, SignedIn);
        await service.CaptureAsync(korea, CancellationToken.None);

        await service.ForgetAsync(korea.Id, CancellationToken.None);

        Assert.False(await service.HasStoredSessionAsync(korea.Id, CancellationToken.None));
        Assert.Equal(
            GameAccountProfileStatus.NotEnrolled,
            await service.GetStatusAsync(korea, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
