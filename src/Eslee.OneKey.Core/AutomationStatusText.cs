namespace Eslee.OneKey.Core;

/// <summary>사용자 화면에 표시하는 자동화 상태 문구. 특정 게임·프로그램 명칭에 결합되지 않는다.</summary>
public static class AutomationStatusText
{
    public static string ForState(AutomationState state, bool waitingForDiscordVoice) => state switch
    {
        AutomationState.Idle => "대기 중",
        AutomationState.Starting => "자동화 시작 중",
        AutomationState.Active => "대상 프로세스 실행 중",
        AutomationState.RestorePending =>
            waitingForDiscordVoice ? "Discord 통화 종료 대기" : "복원 대기",
        AutomationState.Restoring => "복원 중",
        AutomationState.Completed => "복원 완료",
        AutomationState.Failed => "오류",
        _ => state.ToString(),
    };
}
