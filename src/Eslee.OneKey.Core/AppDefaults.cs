namespace Eslee.OneKey.Core;

/// <summary>
/// 제품이 기본으로 쓰는 공개 값입니다. 비밀값은 여기 두지 않습니다.
/// </summary>
public static class AppDefaults
{
    /// <summary>
    /// 로컬 RPC로 Discord에 붙을 때 쓰는 애플리케이션 ID입니다. 공개 값이라
    /// 설정에 넣어 두어도 되지만, 일반 사용자에게 직접 입력하게 하지 않으려고
    /// 제품 기본값으로 둡니다. 사용자가 자기 값을 넣으면 그쪽이 우선합니다.
    /// </summary>
    public const string DiscordRpcClientId = "1525689872621240442";

    /// <summary>설정에 값이 없으면 제품 기본값을 씁니다.</summary>
    public static string ResolveRpcClientId(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DiscordRpcClientId : configured.Trim();
}
