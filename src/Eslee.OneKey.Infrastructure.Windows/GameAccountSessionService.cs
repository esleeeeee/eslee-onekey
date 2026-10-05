using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.ComponentModel;
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
    // Service instances are short-lived (UI and engine); serialize all shared-store changes.
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static async Task<T> SerializedAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await SessionGate.WaitAsync(cancellationToken);
        try { return await operation(); }
        finally { SessionGate.Release(); }
    }

    public Task<bool> CaptureAsync(GameAccountProfile profile, CancellationToken cancellationToken) =>
        SerializedAsync(() => CaptureAsyncCore(profile, cancellationToken), cancellationToken);

    public Task<GameSessionResult> ActivateAsync(GameAccountProfile profile, CancellationToken cancellationToken) =>
        SerializedAsync(() => ActivateAsyncCore(profile, cancellationToken), cancellationToken);

    public Task<GameAccountProfileStatus> GetStatusAsync(GameAccountProfile profile, CancellationToken cancellationToken) =>
        SerializedAsync(() => GetStatusAsyncCore(profile, cancellationToken), cancellationToken);

    public Task<GameSessionResult> PrepareForNewSignInAsync(GameAccountProfile profile, CancellationToken cancellationToken) =>
        SerializedAsync(() => PrepareForNewSignInAsyncCore(profile, cancellationToken), cancellationToken);

    public Task<GameSessionResult> ConfirmActiveAsync(GameAccountProfile profile, CancellationToken cancellationToken) =>
        SerializedAsync(() => ConfirmActiveAsyncCore(profile, cancellationToken), cancellationToken);

    public Task<GameSessionResult> RestorePreparedSessionAsync(GameAccountProfile profile, CancellationToken cancellationToken) =>
        SerializedAsync(() => RestorePreparedSessionAsyncCore(profile, cancellationToken), cancellationToken);

    public Task<GameSessionResult> RestoreLatestCandidateAsync(GameAccountProfile profile, CancellationToken cancellationToken) =>
        SerializedAsync(() => RestoreLatestCandidateAsyncCore(profile, cancellationToken), cancellationToken);

    /// <summary>런처가 뜨고 세션을 읽을 때까지 기다리는 총 시간입니다.</summary>
    private static readonly int LauncherWaitAttempts = 15;

    /// <summary>런처가 저장본을 받아들였는지 지켜보는 총 시간입니다.</summary>
    private static readonly int ConfirmAttempts = 20;

    /// <summary>종료를 요청한 게임이 실제로 사라질 때까지 기다리는 횟수입니다.</summary>
    private static readonly int GameExitWaitAttempts = 20;

    private TimeSpan PollInterval => confirmPollInterval ?? TimeSpan.FromSeconds(1);

    private static string PathKey(string path) => Fingerprint(Path.GetFullPath(path).ToUpperInvariant());
    private string ActiveProfileFile(string path) => Path.Combine(paths.Root, "active-account-" + PathKey(path) + ".json");
    private static string RecoveryKey(string path) => "prepare-" + PathKey(path);

    public async Task<bool> HasStoredSessionAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var stored = await secrets.LoadAccountSessionAsync(profileId, cancellationToken);
        return !string.IsNullOrWhiteSpace(stored);
    }

    private async Task<bool> CaptureAsyncCore(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await MigrateLegacyAsideAsync(profile.SessionFilePath, cancellationToken);
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
        var existing = await ReadActiveMarkerAsync(profile.SessionFilePath, cancellationToken);
        var rejected = existing?.Rejected ?? [];
        rejected.Remove(profile.Id);
        await WriteMarkerAsync(
            new ActiveProfileMarker(profile.Id, Fingerprint(content), rejected, profile.SessionFilePath, Identity(content)),
            cancellationToken);
        secrets.ClearRecovery(RecoveryKey(profile.SessionFilePath));
        secrets.ClearRecovery("candidate-" + PathKey(profile.SessionFilePath) + "-" + Fingerprint(content));
        logger.Info("account-session-captured", "현재 로그인 세션을 프로필에 저장했습니다.");
        return true;
    }

    private async Task<GameSessionResult> ActivateAsyncCore(
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

        await MigrateLegacyAsideAsync(profile.SessionFilePath, cancellationToken);
        var stored = await secrets.LoadAccountSessionAsync(profile.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return new GameSessionResult(
                GameSessionOutcome.NeedsEnrollment,
                "이 계정의 로그인 세션이 저장되지 않았습니다. 해당 계정으로 직접 로그인한 뒤 " +
                "설정에서 현재 세션 저장을 누르세요.");
        }

        var marker = await ReadActiveMarkerAsync(profile.SessionFilePath, cancellationToken);
        var live = await ReadLiveSessionAsync(profile.SessionFilePath, cancellationToken);
        var liveIsSignedIn = live is not null && LooksSignedIn(live);

        // 이미 이 계정이면 런처를 다시 시작하지 않는다. 세션 파일이 그대로거나,
        // 런처가 로그인하며 refresh token을 회전시킨 경우 모두 활성 상태다.
        // 회전본은 되받아 두어야 다음 전환에서 무효가 된 예전 토큰을 넣지 않는다.
        if (marker is not null && marker.ProfileId == profile.Id && live is not null &&
            (Fingerprint(live) == marker.Fingerprint || (liveIsSignedIn && SameIdentity(marker, live))))
        {
            await RecaptureRotatedSessionAsync(marker, live, cancellationToken);
            return new GameSessionResult(GameSessionOutcome.AlreadyActive);
        }

        if (liveIsSignedIn &&
            ((marker is null && Fingerprint(live!) != Fingerprint(stored)) ||
             (marker is not null && Fingerprint(live!) != marker.Fingerprint && !SameIdentity(marker, live!))))
        {
            return await PreserveUnknownAsync(profile, live!, cancellationToken);
        }

        if (!await CloseRunningGameAsync(profile, cancellationToken))
        {
            return new GameSessionResult(
                GameSessionOutcome.BlockedByRunningGame,
                profile.CloseRunningGameToSwitch
                    ? "실행 중인 게임을 종료하지 못해 계정을 전환하지 않았습니다. 게임을 직접 종료한 뒤 다시 시도하세요."
                    : "게임이 실행 중이라 계정을 전환하지 않았습니다. 게임을 종료한 뒤 다시 시도하세요.");
        }

        try
        {
            await CloseLauncherAsync(profile, cancellationToken);
            // Read again after shutdown: launchers may flush rotated credentials on exit.
            live = await ReadLiveSessionAsync(profile.SessionFilePath, cancellationToken);
            if (live is not null && LooksSignedIn(live) &&
                ((marker is null && Fingerprint(live) != Fingerprint(stored)) ||
                 (marker is not null && Fingerprint(live) != marker.Fingerprint && !SameIdentity(marker, live))))
                return await PreserveUnknownAsync(profile, live, cancellationToken);
            liveIsSignedIn = live is not null && LooksSignedIn(live);
            // 1. 지금 활성인 계정의 세션이 갱신됐으면 먼저 되받아 최신으로 보관한다.
            if (marker is not null && liveIsSignedIn)
            {
                marker = await RecaptureRotatedSessionAsync(marker, live!, cancellationToken);
            }

            // 2~3. 런처를 닫고 대상 계정의 저장본을 넣는다.
            Directory.CreateDirectory(Path.GetDirectoryName(profile.SessionFilePath)!);
            await ReplaceSessionAsync(profile.SessionFilePath, stored,
                new ActiveProfileMarker(profile.Id, Fingerprint(stored), marker?.Rejected ?? [], profile.SessionFilePath, Identity(stored)),
                cancellationToken);
            secrets.ClearRecovery(RecoveryKey(profile.SessionFilePath));
            logger.Info("account-session-activated", "지정한 계정의 로그인 세션으로 전환했습니다.");
            return new GameSessionResult(GameSessionOutcome.Switched);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            logger.Error("account-session-activate-failed", exception, "계정 전환에 실패했습니다.");
            return new GameSessionResult(
                GameSessionOutcome.Failed,
                "계정 전환에 실패했습니다. 런처가 완전히 종료됐는지 확인하세요.");
        }
    }

    private async Task<GameAccountProfileStatus> GetStatusAsyncCore(
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
        var marker = await ReadActiveMarkerAsync(profile.SessionFilePath, cancellationToken);
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
    private async Task<GameSessionResult> PrepareForNewSignInAsyncCore(
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
            await CloseLauncherAsync(profile, cancellationToken);
            await MigrateLegacyAsideAsync(profile.SessionFilePath, cancellationToken);
            var marker = await ReadActiveMarkerAsync(profile.SessionFilePath, cancellationToken);
            var key = RecoveryKey(profile.SessionFilePath);
            // Never overwrite an unfinished preparation; restore or explicitly capture first.
            if (await secrets.LoadRecoveryAsync(key, cancellationToken) is not null)
                return new GameSessionResult(GameSessionOutcome.Unknown,
                    "이전 로그인 준비 복구본이 있습니다. 로그인 준비 취소/복원을 누르거나 현재 계정을 등록하세요.");
            if (File.Exists(profile.SessionFilePath))
            {
                var live = await File.ReadAllTextAsync(profile.SessionFilePath, cancellationToken);
                await secrets.SaveRecoveryAsync(key,
                    JsonSerializer.Serialize(new Preparation(live, marker)), cancellationToken);
                if (marker is not null && LooksSignedIn(live) &&
                    (Fingerprint(live) == marker.Fingerprint || SameIdentity(marker, live)))
                    await RecaptureRotatedSessionAsync(marker, live, cancellationToken);
                else if (LooksSignedIn(live))
                    await PreserveUnknownAsync(profile, live, cancellationToken);
                File.Delete(profile.SessionFilePath);
            }
            await ClearActiveMarkerAsync(profile.SessionFilePath, cancellationToken);
            logger.Info("account-signin-prepared", "로그인되지 않은 상태를 준비했습니다.");
            return new GameSessionResult(GameSessionOutcome.Switched);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
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

        if (!SameIdentity(marker, live)) return marker;
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
    private async Task<GameSessionResult> ConfirmActiveAsyncCore(
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

        var marker = await ReadActiveMarkerAsync(profile.SessionFilePath, cancellationToken);
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
                await RecordRejectionAsync(profile, cancellationToken);
                logger.Warning("account-login-rejected", "런처가 저장된 로그인 세션을 거부했습니다.");
                return new GameSessionResult(
                    GameSessionOutcome.NeedsEnrollment,
                    "런처가 저장된 세션을 거부했습니다. 이 계정으로 직접 로그인한 뒤 다시 등록하세요.");
            }

            // 런처가 로그인하면서 토큰을 회전시키면 파일 내용이 바뀝니다. 그 순간이
            // 저장본을 실제로 받아들였다는 신호이므로, 회전본을 되받고 끝냅니다.
            if (marker is not null && marker.ProfileId == profile.Id && Fingerprint(live) != marker.Fingerprint)
            {
                if (!SameIdentity(marker, live))
                    return await PreserveUnknownAsync(profile, live, cancellationToken);
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

    private async Task RecordRejectionAsync(GameAccountProfile profile, CancellationToken cancellationToken)
    {
        var marker = await ReadActiveMarkerAsync(profile.SessionFilePath, cancellationToken);
        if (marker is null)
        {
            return;
        }

        var rejected = marker.Rejected ?? [];
        if (!rejected.Contains(profile.Id))
        {
            rejected.Add(profile.Id);
            await WriteMarkerAsync(marker with { Rejected = rejected }, cancellationToken);
        }
    }

    private static async Task<string?> ReadLiveSessionAsync(
        string path,
        CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;

    public async Task ForgetAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await SessionGate.WaitAsync(cancellationToken);
        try { await secrets.ClearAccountSessionAsync(profileId, cancellationToken); }
        finally { SessionGate.Release(); }
    }

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
            await Task.Delay(confirmPollInterval ?? TimeSpan.FromSeconds(2), cancellationToken);
        }
        foreach (var name in profile.LauncherProcessNames)
            if (await processes.IsRunningAsync(name, cancellationToken))
                throw new IOException("런처 종료를 확인하지 못했습니다.");
    }

    /// <summary>
    /// 게임이 실행 중이 아니면 true입니다. 실행 중이면 프로필이 허락한 경우에만 종료하고,
    /// 정말 사라졌는지 확인한 뒤 true를 돌려줍니다. 게임이 살아 있는 채로 세션을 바꾸면
    /// 런처가 예전 계정으로 파일을 다시 써 버립니다.
    /// </summary>
    private async Task<bool> CloseRunningGameAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken)
    {
        var running = new List<string>();
        foreach (var name in profile.BlockingProcessNames)
        {
            if (await processes.IsRunningAsync(name, cancellationToken))
            {
                running.Add(name);
            }
        }

        if (running.Count == 0)
        {
            return true;
        }
        if (!profile.CloseRunningGameToSwitch)
        {
            return false;
        }

        logger.Info("account-switch-closing-game", "계정을 바꾸기 위해 실행 중인 게임을 종료합니다.");
        foreach (var name in running)
        {
            try
            {
                await processes.StopAsync(name, cancellationToken);
            }
            catch (Exception exception) when (exception is Win32Exception
                or InvalidOperationException
                or NotSupportedException)
            {
                // 보호된 프로세스는 직접 못 끌 수 있다. 런처를 닫으면 따라 꺼지므로 아래에서 확인한다.
                logger.Warning(
                    "account-switch-close-game-failed",
                    $"게임 프로세스를 직접 종료하지 못했습니다. ({exception.GetType().Name})");
            }
        }

        // 게임은 런처에 붙어 있어서 런처가 사라지면 스스로 종료한다.
        try
        {
            await CloseLauncherAsync(profile, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or Win32Exception)
        {
            return false;
        }

        for (var attempt = 0; attempt < GameExitWaitAttempts; attempt++)
        {
            var alive = false;
            foreach (var name in running)
            {
                alive |= await processes.IsRunningAsync(name, cancellationToken);
            }
            if (!alive)
            {
                return true;
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
        return false;
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
        await AtomicSessionFile.WriteAsync(
            ActiveProfileFile(marker.SessionPath!),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(marker)), cancellationToken);
    }

    private Task ClearActiveMarkerAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(ActiveProfileFile(path)))
        {
            File.Delete(ActiveProfileFile(path));
        }
        return Task.CompletedTask;
    }

    private async Task<ActiveProfileMarker?> ReadActiveMarkerAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(ActiveProfileFile(path)))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ActiveProfileMarker>(
                await File.ReadAllTextAsync(ActiveProfileFile(path), cancellationToken));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Preparation(string? Content, ActiveProfileMarker? Marker);

    private async Task ReplaceSessionAsync(string path, string content, ActiveProfileMarker marker, CancellationToken cancellationToken)
    {
        var previous = new Preparation(await ReadLiveSessionAsync(path, cancellationToken),
            await ReadActiveMarkerAsync(path, cancellationToken));
        var key = "switch-" + PathKey(path);
        if (await secrets.LoadRecoveryAsync(key, cancellationToken) is not null)
            throw new IOException("이전 전환의 복구본이 있습니다. 로그인 준비 취소/복원으로 복원하세요.");
        await secrets.SaveRecoveryAsync(key, JsonSerializer.Serialize(previous), cancellationToken);
        try
        {
            await AtomicSessionFile.WriteAsync(path, Encoding.UTF8.GetBytes(content), cancellationToken);
            await WriteMarkerAsync(marker, cancellationToken);
        }
        catch
        {
            // Cancellation must not interrupt rollback. If rollback fails, the encrypted journal remains.
            if (previous.Content is null) File.Delete(path);
            else await AtomicSessionFile.WriteAsync(path, Encoding.UTF8.GetBytes(previous.Content), CancellationToken.None);
            if (previous.Marker is null) await ClearActiveMarkerAsync(path, CancellationToken.None);
            else await WriteMarkerAsync(previous.Marker, CancellationToken.None);
            secrets.ClearRecovery(key);
            throw;
        }
        secrets.ClearRecovery(key);
    }

    private async Task<GameSessionResult> RestorePreparedSessionAsyncCore(GameAccountProfile profile, CancellationToken cancellationToken)
    {
        foreach (var name in profile.BlockingProcessNames)
            if (await processes.IsRunningAsync(name, cancellationToken))
                return new GameSessionResult(GameSessionOutcome.BlockedByRunningGame);
        try
        {
            var key = RecoveryKey(profile.SessionFilePath);
            var payload = await secrets.LoadRecoveryAsync(key, cancellationToken);
            if (payload is null)
            {
                key = "switch-" + PathKey(profile.SessionFilePath);
                payload = await secrets.LoadRecoveryAsync(key, cancellationToken);
            }
            if (payload is null) return new GameSessionResult(GameSessionOutcome.NotConfigured, "복원할 로그인 준비가 없습니다.");
            var backup = JsonSerializer.Deserialize<Preparation>(payload)!;
            await CloseLauncherAsync(profile, cancellationToken);
            var current = await ReadLiveSessionAsync(profile.SessionFilePath, cancellationToken);
            if (current is not null && current != backup.Content)
                await PreserveUnknownAsync(profile, current, cancellationToken);
            if (backup.Content is null) File.Delete(profile.SessionFilePath);
            else await AtomicSessionFile.WriteAsync(profile.SessionFilePath, Encoding.UTF8.GetBytes(backup.Content), cancellationToken);
            if (backup.Marker is not null) await WriteMarkerAsync(backup.Marker, cancellationToken);
            else await ClearActiveMarkerAsync(profile.SessionFilePath, cancellationToken);
            secrets.ClearRecovery(key);
            return new GameSessionResult(GameSessionOutcome.Switched);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new GameSessionResult(GameSessionOutcome.Failed, "복원 실패: 런처 종료와 파일 권한을 확인하세요. 암호화 복구본은 유지됩니다.");
        }
    }

    private async Task<GameSessionResult> RestoreLatestCandidateAsyncCore(GameAccountProfile profile, CancellationToken cancellationToken)
    {
        foreach (var name in profile.BlockingProcessNames)
            if (await processes.IsRunningAsync(name, cancellationToken))
                return new GameSessionResult(GameSessionOutcome.BlockedByRunningGame);
        await MigrateLegacyAsideAsync(profile.SessionFilePath, cancellationToken);
        var key = secrets.LatestRecoveryKey("candidate-" + PathKey(profile.SessionFilePath) + "-");
        if (key is null) return new GameSessionResult(GameSessionOutcome.NotConfigured, "암호화 후보가 없습니다.");
        var content = await secrets.LoadRecoveryAsync(key, cancellationToken);
        await CloseLauncherAsync(profile, cancellationToken);
        var current = await ReadLiveSessionAsync(profile.SessionFilePath, cancellationToken);
        if (current is not null && current != content)
            await PreserveUnknownAsync(profile, current, cancellationToken);
        await AtomicSessionFile.WriteAsync(profile.SessionFilePath, Encoding.UTF8.GetBytes(content!), cancellationToken);
        await ClearActiveMarkerAsync(profile.SessionFilePath, cancellationToken);
        // Keep the candidate until explicit capture; restoring does not attest its account.
        return new GameSessionResult(GameSessionOutcome.Switched, "후보를 복원했습니다. 런처에서 계정을 확인한 뒤 해당 프로필에 등록하세요.");
    }

    private async Task MigrateLegacyAsideAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var aside = path + ".onekey-aside";
        if (!File.Exists(aside)) return;
        var content = await File.ReadAllTextAsync(aside, cancellationToken);
        await secrets.SaveRecoveryAsync("candidate-" + PathKey(path) + "-" + Fingerprint(content), content, cancellationToken);
        // Delete only after the encrypted atomic write succeeded.
        File.Delete(aside);
    }

    private async Task<GameSessionResult> PreserveUnknownAsync(GameAccountProfile profile, string live, CancellationToken cancellationToken)
    {
        await secrets.SaveRecoveryAsync("candidate-" + PathKey(profile.SessionFilePath) + "-" + Fingerprint(live), live, cancellationToken);
        return new GameSessionResult(GameSessionOutcome.Unknown,
            "현재 계정을 식별할 수 없어 기존 보관본을 유지하고 암호화 후보를 보존했습니다. 런처에서 계정을 확인한 뒤 해당 자동화의 현재 로그인 계정 등록을 누르세요.");
    }

    private static bool SameIdentity(ActiveProfileMarker marker, string live) =>
        marker.AccountIdentity is not null && marker.AccountIdentity == Identity(live);

    // Local confusion guard, NOT cryptographic authentication. Only explicit JWT issuer/subject
    // pairs are usable; opaque refresh tokens and display names cannot identify an account.
    private static string? Identity(string content)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(content,
            """(?:id_token|refresh_token)["']?\s*:\s*["']?([A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)"""))
        {
            try
            {
                var payload = match.Groups[1].Value.Split('.')[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
                var root = document.RootElement;
                if (!root.TryGetProperty("iss", out var issuer) || !root.TryGetProperty("sub", out var subject) ||
                    issuer.ValueKind != JsonValueKind.String || subject.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(issuer.GetString()) || string.IsNullOrWhiteSpace(subject.GetString())) continue;
                identities.Add(Fingerprint(JsonSerializer.Serialize(new[] { issuer.GetString(), subject.GetString() })));
            }
            catch (Exception exception) when (exception is FormatException or JsonException) { }
        }
        return identities.Count == 1 ? identities.Single() : null;
    }

    private sealed record ActiveProfileMarker(
        Guid ProfileId,
        string Fingerprint,
        List<Guid>? Rejected = null, string? SessionPath = null, string? AccountIdentity = null);
}
