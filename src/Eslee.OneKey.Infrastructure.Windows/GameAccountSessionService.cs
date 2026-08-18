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
    IAppLogger logger) : IGameSessionService
{
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
        await WriteActiveMarkerAsync(profile.Id, Fingerprint(content), cancellationToken);
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
        var liveFingerprint = await ReadFingerprintAsync(profile.SessionFilePath, cancellationToken);

        // 이미 이 계정이 활성이면 로그아웃도 재로그인도 하지 않는다.
        if (marker is not null &&
            marker.ProfileId == profile.Id &&
            liveFingerprint is not null &&
            marker.Fingerprint == liveFingerprint)
        {
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
            // 지금 활성인 프로필의 세션이 갱신됐으면 먼저 되받아 최신으로 보관한다.
            if (marker is not null &&
                marker.ProfileId != profile.Id &&
                liveFingerprint is not null &&
                marker.Fingerprint != liveFingerprint)
            {
                var live = await File.ReadAllTextAsync(profile.SessionFilePath, cancellationToken);
                await secrets.SaveAccountSessionAsync(marker.ProfileId, live, cancellationToken);
            }

            await CloseLauncherAsync(profile, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(profile.SessionFilePath)!);
            await File.WriteAllTextAsync(profile.SessionFilePath, stored, cancellationToken);
            await WriteActiveMarkerAsync(profile.Id, Fingerprint(stored), cancellationToken);
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

    private async Task WriteActiveMarkerAsync(
        Guid profileId,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        var json = JsonSerializer.Serialize(new ActiveProfileMarker(profileId, fingerprint));
        await File.WriteAllTextAsync(ActiveProfileFile, json, cancellationToken);
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

    private sealed record ActiveProfileMarker(Guid ProfileId, string Fingerprint);
}
