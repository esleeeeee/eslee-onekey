namespace Eslee.OneKey.Core;

/// <summary>
/// 자동화를 어떤 게임 계정으로 시작할지 고르는 프로필입니다. 비밀번호는 담지
/// 않습니다. 계정 전환은 런처가 이미 보관하는 로그인 세션 파일을 프로필별로 따로
/// 두는 방식이며, 최초 등록은 사용자가 그 계정으로 직접 로그인한 뒤 저장하는
/// 것으로 끝납니다. 특정 게임에 묶이지 않도록 경로와 프로세스 이름은 모두
/// 설정값으로 받습니다.
/// </summary>
public sealed record GameAccountProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>사용자에게 보여줄 이름입니다.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>이 계정으로 자동화를 시작하는 전역 단축키입니다.</summary>
    public HotkeyGesture Hotkey { get; init; } = new();

    /// <summary>런처가 로그인 세션을 보관하는 파일의 전체 경로입니다.</summary>
    public string SessionFilePath { get; init; } = string.Empty;

    /// <summary>세션을 바꾸기 전에 닫아야 하는 런처 프로세스 이름입니다.</summary>
    public List<string> LauncherProcessNames { get; init; } = [];

    /// <summary>이 프로세스가 살아 있으면 계정을 전환하지 않습니다(실행 중인 게임).</summary>
    public List<string> BlockingProcessNames { get; init; } = [];
}

public enum GameSessionOutcome
{
    /// <summary>계정 전환 없이 그대로 진행합니다(프로필 미지정).</summary>
    NotRequested,
    /// <summary>이미 이 계정이 활성이라 아무것도 하지 않았습니다.</summary>
    AlreadyActive,
    /// <summary>저장된 세션으로 전환했습니다.</summary>
    Switched,
    /// <summary>저장된 세션이 없어 사용자의 최초 로그인이 필요합니다.</summary>
    NeedsEnrollment,
    /// <summary>게임이 실행 중이라 전환하지 않았습니다.</summary>
    BlockedByRunningGame,
    /// <summary>프로필 설정이 비어 있어 전환할 수 없습니다.</summary>
    NotConfigured,
    /// <summary>전환에 실패했습니다.</summary>
    Failed,
}

public sealed record GameSessionResult(GameSessionOutcome Outcome, string? Message = null)
{
    public bool CanContinue =>
        Outcome is GameSessionOutcome.NotRequested
            or GameSessionOutcome.AlreadyActive
            or GameSessionOutcome.Switched;
}

/// <summary>
/// 런처의 로그인 세션을 프로필별로 보관하고 원하는 프로필로 활성화합니다.
/// 구현은 게임 설치 파일이 아니라 사용자 데이터 폴더의 세션만 다룹니다.
/// </summary>
public interface IGameSessionService
{
    /// <summary>지금 런처에 남아 있는 세션을 이 프로필로 저장합니다.</summary>
    Task<bool> CaptureAsync(GameAccountProfile profile, CancellationToken cancellationToken);

    /// <summary>이 프로필의 세션을 런처가 쓰도록 활성화합니다.</summary>
    Task<GameSessionResult> ActivateAsync(
        GameAccountProfile profile,
        CancellationToken cancellationToken);

    /// <summary>이 프로필에 저장된 세션이 있는지 여부입니다.</summary>
    Task<bool> HasStoredSessionAsync(Guid profileId, CancellationToken cancellationToken);
}
