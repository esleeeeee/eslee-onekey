using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eslee.OneKey.Core;

namespace Eslee.OneKey.Infrastructure.Windows;

/// <summary>
/// 런처의 로그인 세션 파일을 프로필별로 보관해 계정을 전환합니다.
///
/// 게임 런처는 보통 로그인 상태를 사용자 데이터 폴더의 세션 파일에 보관합니다.
/// 계정마다 그 파일을 따로 두었다가 실행 직전에 바꿔 넣으면 비밀번호 없이 원하는
/// 계정으로 로그인된 상태가 됩니다. 게임 설치 파일이나 보호 드라이버는 건드리지
/// 않고, 2단계 인증이나 사람 확인은 최초 등록 때 사용자가 직접 처리하므로 우회가
/// 없습니다. 어떤 파일과 프로세스를 다룰지는 전부 프로필 설정에서 받습니다.
///
/// 보관본은 세션 쿠키라 비밀값과 같으므로 DPAPI로만 암호화해 저장합니다.
/// </summary>
public sealed class GameAccountSessionService(
    ApplicationPaths paths,
    DpapiSecretStore secrets,
    IProcessService processes,
    IAppLogger logger,
    TimeSpan? confirmPollInterval = null) : IGameSessionService
{
    /// <summary>런처가 뜨고 세션을 읽을 때까지 기다리는 총 시간입니다.</summary>
    private static readonly int LauncherWaitAttempts = 15;

    /// <summary>런처가 저장본을 받아들였는지 지켜보는 총 시간입니다.</summary>
    private static readonly int ConfirmAttempts = 20;

    private TimeSpan PollInterval => confirmPollInterval ?? TimeSpan.FromSeconds(1);

    private string ActiveProfileFile => Path.Combine(paths.Root, "active-account-profile.json");

    public async Task<bool> HasStoredSessionAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var stored = await secrets.LoadAccountSessionAsync(profileId, cancellationToken);
        return !string.IsNullOrWhiteSpace(stored);
    }

    public async Task<bool> CaptureAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.SessionFilePath) ||
            !File.Exists(profile.SessionFilePath))
        {
            logger.Warning("account-session-missing", "런처 로그인 세션 파일을 찾지 못했습니다.");
            return false;
        }

        var content = await File.ReadAllTextAsync(profile.SessionFilePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        await secrets.SaveAccountSessionAsync(profile.Id, content, cancellationToken);
        var existing = await ReadActiveMarkerAsync(cancellationToken);
        var rejected = existing?.Rejected ?? [];
        rejected.Remove(profile.Id);
        await WriteMarkerAsync(
            new ActiveProfileMarker(profile.Id, Fingerprint(content), rejected),
            cancellationToken);
        logger.Info("account-session-captured", "현재 로그인 세션을 프로필에 저장했습니다.");
        return true;
    }

    public async Task<GameSessionResult> ActivateAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.SessionFilePath))
        {
            return new GameSessionResult(
                GameSessionOutcome.NotConfigured,
                "이 프로필에 세션 파일 경로가 설정되지 않았습니다.");
        }

        var stored = await secrets.LoadAccountSessionAsync(profile.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return new GameSessionResult(
                GameSessionOutcome.NeedsEnrollment,
                "이 계정의 로그인 세션이 저장되지 않았습니다. 해당 계정으로 직접 로그인한 뒤 " +
                "설정에서 현재 세션 저장을 누르세요.");
        }

        var marker = await ReadActiveMarkerAsync(cancellationToken);
        var live = await ReadLiveSessionAsync(profile.SessionFilePath, cancellationToken);
        var liveIsSignedIn = live is not null && LooksSignedIn(live);

        // 이미 이 계정이면 런처를 다시 시작하지 않는다. 세션 파일이 그대로거나,
        // 런처가 로그인하며 refresh token을 회전시킨 경우 모두 활성 상태다.
        // 회전본은 되받아 두어야 다음 전환에서 무효가 된 예전 토큰을 넣지 않는다.
        if (marker is not null && marker.ProfileId == profile.Id && live is not null &&
            (Fingerprint(live) == marker.Fingerprint || liveIsSignedIn))
        {
            await RecaptureRotatedSessionAsync(marker, live, cancellationToken);
            return new GameSessionResult(GameSessionOutcome.AlreadyActive);
        }

        foreach (var process in profile.BlockingProcessNames)
        {
            if (await processes.IsRunningAsync(process, cancellationToken))
            {
                return new GameSessionResult(
                    GameSessionOutcome.BlockedByRunningGame,
                    "게임이 실행 중이라 계정을 전환하지 않았습니다. 게임을 종료한 뒤 다시 시도하세요.");
            }
        }

        try
        {
            // 1. 지금 활성인 계정의 세션이 갱신됐으면 먼저 되받아 최신으로 보관한다.
            if (marker is not null && liveIsSignedIn)
            {
                marker = await RecaptureRotatedSessionAsync(marker, live!, cancellationToken);
            }

            // 2~3. 런처를 닫고 대상 계정의 저장본을 넣는다.
            await CloseLauncherAsync(profile, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(profile.SessionFilePath)!);
            await File.WriteAllTextAsync(profile.SessionFilePath, stored, cancellationToken);
            await WriteMarkerAsync(
                new ActiveProfileMarker(profile.Id, Fingerprint(stored), marker?.Rejected ?? []),
                cancellationToken);
            logger.Info("account-session-activated", "지정한 계정의 로그인 세션으로 전환했습니다.");
            return new GameSessionResult(GameSessionOutcome.Switched);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.Error("account-session-activate-failed", exception, "계정 전환에 실패했습니다.");
            return new GameSessionResult(
                GameSessionOutcome.Failed,
                "계정 전환에 실패했습니다. 런처가 완전히 종료됐는지 확인하세요.");
        }
    }

    public async Task<GameAccountProfileStatus> GetStatusAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!await HasStoredSessionAsync(profile.Id, cancellationToken))
        {
            return GameAccountProfileStatus.NotEnrolled;
        }

        var marker = await DetectRejectionAsync(profile, cancellationToken);
        return marker?.Rejected?.Contains(profile.Id) == true
            ? GameAccountProfileStatus.NeedsReenrollment
            : GameAccountProfileStatus.Enrolled;
    }

    /// <summary>
    /// 활성으로 표시된 프로필의 세션을 런처가 지워 버렸다면, 그 저장본은 서버에서
    /// 이미 무효가 된 것입니다. 다시 등록해야 한다고 기록해 둡니다.
    /// </summary>
    private async Task<ActiveProfileMarker?> DetectRejectionAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        var marker = await ReadActiveMarkerAsync(cancellationToken);
        if (marker is null || string.IsNullOrWhiteSpace(profile.SessionFilePath))
        {
            return marker;
        }

        var live = File.Exists(profile.SessionFilePath)
            ? await File.ReadAllTextAsync(profile.SessionFilePath, cancellationToken)
            : null;
        var rejected = marker.Rejected ?? [];
        if (live is not null && !LooksSignedIn(live) && !rejected.Contains(marker.ProfileId))
        {
            rejected.Add(marker.ProfileId);
            marker = marker with { Rejected = rejected };
            await WriteMarkerAsync(marker, cancellationToken);
        }
        return marker;
    }

    /// <summary>
    /// 다음 계정을 등록할 수 있도록 로그인되지 않은 상태를 만듭니다. 런처의 로그아웃
    /// 명령은 쓰지 않습니다. 실측 결과 로그아웃은 서버에서 refresh token을 폐기해
    /// 이미 등록해 둔 다른 계정의 저장본까지 무효로 만듭니다.
    /// </summary>
    public async Task<GameSessionResult> PrepareForNewSignInAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.SessionFilePath))
        {
            return new GameSessionResult(
                GameSessionOutcome.NotConfigured,
                "이 프로필에 세션 파일 경로가 설정되지 않았습니다.");
        }

        foreach (var process in profile.BlockingProcessNames)
        {
            if (await processes.IsRunningAsync(process, cancellationToken))
            {
                return new GameSessionResult(
                    GameSessionOutcome.BlockedByRunningGame,
                    "게임이 실행 중입니다. 게임을 종료한 뒤 다시 시도하세요.");
            }
        }

        try
        {
            // 지금 로그인된 계정을 잃지 않도록 먼저 해당 프로필로 되받아 둔다.
            var marker = await ReadActiveMarkerAsync(cancellationToken);
            if (marker is not null && File.Exists(profile.SessionFilePath))
            {
                var live = await File.ReadAllTextAsync(profile.SessionFilePath, cancellationToken);
                if (LooksSignedIn(live))
                {
                    await secrets.SaveAccountSessionAsync(marker.ProfileId, live, cancellationToken);
                }
            }

            await CloseLauncherAsync(profile, cancellationToken);
            if (File.Exists(profile.SessionFilePath))
            {
                // 지우지 않고 옆으로 치워 둔다. 되돌릴 수 있어야 한다.
                var asideFile = profile.SessionFilePath + ".onekey-aside";
                File.Move(profile.SessionFilePath, asideFile, overwrite: true);
            }

            await ClearActiveMarkerAsync(cancellationToken);
            logger.Info("account-signin-prepared", "로그인되지 않은 상태를 준비했습니다.");
            return new GameSessionResult(GameSessionOutcome.Switched);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.Error("account-signin-prepare-failed", exception, "로그인 준비에 실패했습니다.");
            return new GameSessionResult(
                GameSessionOutcome.Failed,
                "로그인 준비에 실패했습니다. 런처가 완전히 종료됐는지 확인하세요.");
        }
    }

    /// <summary>
    /// 런처는 로그인할 때마다 refresh token을 회전시키고 세션 파일을 다시 씁니다.
    /// 회전본을 되받아 두지 않으면 다음 전환에서 이미 무효가 된 예전 토큰을 되돌려
    /// 넣게 됩니다. 어느 프로필로 되받을지는 활성 마커가 알려 줍니다.
    /// </summary>
    private async Task<ActiveProfileMarker> RecaptureRotatedSessionAsync(
        ActiveProfileMarker marker,
        string live,
        CancellationToken cancellationToken)
    {
        var liveFingerprint = Fingerprint(live);
        if (marker.Fingerprint == liveFingerprint)
        {
            return marker;
        }

        await secrets.SaveAccountSessionAsync(marker.ProfileId, live, cancellationToken);
        var refreshed = marker with { Fingerprint = liveFingerprint };
        await WriteMarkerAsync(refreshed, cancellationToken);
        logger.Info("account-session-refreshed", "갱신된 로그인 세션을 활성 프로필에 되받았습니다.");
        return refreshed;
    }

    /// <summary>
    /// 세션을 바꿔 넣고 런처를 다시 띄운 뒤, 런처가 그 세션을 받아들였는지 봅니다.
    /// 런처가 거부하면 세션 파일에서 로그인 유지 토큰을 지우므로 그것으로 판정합니다.
    /// 확실한 신호가 없으면 실패로 몰지 않고 판정을 보류합니다.
    /// </summary>
    public async Task<GameSessionResult> ConfirmActiveAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.SessionFilePath))
        {
            return new GameSessionResult(
                GameSessionOutcome.NotConfigured,
                "이 프로필에 세션 파일 경로가 설정되지 않았습니다.");
        }

        if (!await WaitForLauncherAsync(profile, cancellationToken))
        {
            logger.Info("account-login-unconfirmed", "런처가 뜨지 않아 로그인 상태를 확인하지 못했습니다.");
            return new GameSessionResult(GameSessionOutcome.Switched);
        }

        var marker = await ReadActiveMarkerAsync(cancellationToken);
        for (var attempt = 0; attempt < ConfirmAttempts; attempt++)
        {
            await Task.Delay(PollInterval, cancellationToken);
            var live = await ReadLiveSessionAsync(profile.SessionFilePath, cancellationToken);
            if (live is null)
            {
                continue;
            }

            if (!LooksSignedIn(live))
            {
                await RecordRejectionAsync(profile.Id, cancellationToken);
                logger.Warning("account-login-rejected", "런처가 저장된 로그인 세션을 거부했습니다.");
                return new GameSessionResult(
                    GameSessionOutcome.NeedsEnrollment,
                    "런처가 저장된 세션을 거부했습니다. 이 계정으로 직접 로그인한 뒤 다시 등록하세요.");
            }

            // 런처가 로그인하면서 토큰을 회전시키면 파일 내용이 바뀝니다. 그 순간이
            // 저장본을 실제로 받아들였다는 신호이므로, 회전본을 되받고 끝냅니다.
            if (marker is not null && Fingerprint(live) != marker.Fingerprint)
            {
                await RecaptureRotatedSessionAsync(marker, live, cancellationToken);
                logger.Info("account-login-confirmed", "대상 계정으로 로그인된 것을 확인했습니다.");
                return new GameSessionResult(GameSessionOutcome.Switched);
            }
        }

        logger.Info("account-login-unconfirmed", "정해진 시간 안에 로그인 여부를 판정하지 못했습니다.");
        return new GameSessionResult(GameSessionOutcome.Switched);
    }

    private async Task<bool> WaitForLauncherAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.LauncherProcessNames.Count == 0)
        {
            return false;
        }

        for (var attempt = 0; attempt < LauncherWaitAttempts; attempt++)
        {
            foreach (var name in profile.LauncherProcessNames)
            {
                if (await processes.IsRunningAsync(name, cancellationToken))
                {
                    return true;
                }
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
        return false;
    }

    private async Task RecordRejectionAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var marker = await ReadActiveMarkerAsync(cancellationToken);
        if (marker is null)
        {
            return;
        }

        var rejected = marker.Rejected ?? [];
        if (!rejected.Contains(profileId))
        {
            rejected.Add(profileId);
            await WriteMarkerAsync(marker with { Rejected = rejected }, cancellationToken);
        }
    }

    private static async Task<string?> ReadLiveSessionAsync(
        string path,
        CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;

    public Task ForgetAsync(Guid profileId, CancellationToken cancellationToken) =>
        secrets.ClearAccountSessionAsync(profileId, cancellationToken);

    /// <summary>
    /// 세션 파일에 로그인 유지 토큰이 들어 있는지 봅니다. 값은 읽지 않고 존재만
    /// 확인합니다. 런처가 토큰을 거부하면 이 블록을 지웁니다.
    /// </summary>
    private static bool LooksSignedIn(string content) =>
        content.Contains("refresh_token", StringComparison.Ordinal);

    /// <summary>
    /// 런처는 종료할 때 세션 파일을 다시 쓰므로 바꿔 넣기 전에 닫아야 합니다.
    /// 실행 중인 게임은 앞에서 이미 걸러냈습니다.
    /// </summary>
    private async Task CloseLauncherAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        var closedAny = false;
        foreach (var name in profile.LauncherProcessNames)
        {
            if (await processes.IsRunningAsync(name, cancellationToken))
            {
                await processes.StopAsync(name, cancellationToken);
                closedAny = true;
            }
        }

        if (closedAny)
        {
            // 종료 시 기록이 끝나도록 잠깐 기다린다.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static async Task<string?> ReadFingerprintAsync(
        string path,
        CancellationToken cancellationToken) =>
        File.Exists(path)
            ? Fingerprint(await File.ReadAllTextAsync(path, cancellationToken))
            : null;

    /// <summary>세션 내용이 아니라 해시만 남깁니다. 비밀값을 평문으로 두지 않습니다.</summary>
    private static string Fingerprint(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private async Task WriteMarkerAsync(
        ActiveProfileMarker marker,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            ActiveProfileFile,
            JsonSerializer.Serialize(marker),
            cancellationToken);
    }

    private Task ClearActiveMarkerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(ActiveProfileFile))
        {
            File.Delete(ActiveProfileFile);
        }
        return Task.CompletedTask;
    }

    private async Task<ActiveProfileMarker?> ReadActiveMarkerAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ActiveProfileFile))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ActiveProfileMarker>(
                await File.ReadAllTextAsync(ActiveProfileFile, cancellationToken));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ActiveProfileMarker(
        Guid ProfileId,
        string Fingerprint,
        List<Guid>? Rejected = null);
}
