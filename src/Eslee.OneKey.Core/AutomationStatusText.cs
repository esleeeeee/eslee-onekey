namespace Eslee.OneKey.Core;

/// <summary>사용자 화면에 표시하는 자동화 상태 문구. 특정 게임·프로그램 명칭에 결합되지 않는다.</summary>
public static class AutomationStatusText
{
    /// <param name="keptCurrentDevice">
    /// 복원 대신 현재 오디오 장치를 그대로 둔 경우입니다. 완료 문구가 실제로
    /// 복원했다고 잘못 알리지 않도록 구분합니다.
    /// </param>
    public static string ForState(
        AutomationState state,
        bool waitingForDiscordVoice,
        bool keptCurrentDevice = false) => state switch
    {
        AutomationState.Idle => "대기 중",
        AutomationState.Starting => "자동화 시작 중",
        AutomationState.Active => "대상 프로세스 실행 중",
        AutomationState.RestorePending =>
            waitingForDiscordVoice ? "Discord 통화 종료 대기" : "복원 대기",
        AutomationState.Restoring => "복원 중",
        AutomationState.Completed => keptCurrentDevice ? "현재 장치 유지" : "복원 완료",
        AutomationState.Failed => "오류",
        _ => state.ToString(),
    };
}
