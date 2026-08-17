using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eslee.OneKey.Infrastructure.Windows;

/// <summary>
/// settings.json 스키마 마이그레이션. v1은 초기 MVP의 게임 중심 속성명
/// (gameProcessName, gameExecutablePath)을 사용했고 Discord 연동이 항상 켜져
/// 있었다. v2는 범용 속성명(watchProcessName, launchExecutablePath)과
/// 명시적 useDiscordIntegration 플래그를 사용한다. v3은 종료 후 오디오
/// 자동 복원을 restoreAudioOnExit 옵션으로 분리한다.
/// 기존 파일을 읽지 못해 설정이 초기화되는 일이 없도록 로드 시 변환한다.
/// </summary>
public static class SettingsMigration
{
    public const int CurrentSchemaVersion = 4;

    public static JsonNode Migrate(JsonNode? root)
    {
        if (root is not JsonObject settings)
        {
            // 구문 오류 JSON과 동일하게 초기화 실패로 표면화한다.
            // 조용히 기본값으로 대체하면 다음 저장에서 원본이 유실된다.
            throw new JsonException("settings.json의 루트가 JSON 객체가 아닙니다.");
        }

        var version = ReadSchemaVersion(settings);
        if (version >= CurrentSchemaVersion)
        {
            return settings;
        }

        if (settings["automations"] is JsonArray automations)
        {
            foreach (var automation in automations.OfType<JsonObject>())
            {
                if (version < 2)
                {
                    MigrateAutomationFromV1(automation);
                }
                if (version < 3)
                {
                    MigrateAutomationToV3(automation);
                }
                MigrateAutomationToV4(automation);
            }
        }
        settings["schemaVersion"] = CurrentSchemaVersion;
        return settings;
    }

    private static int ReadSchemaVersion(JsonObject settings) =>
        settings["schemaVersion"] is JsonValue value && value.TryGetValue<int>(out var version)
            ? version
            : 1;

    private static void MigrateAutomationFromV1(JsonObject automation)
    {
        RenameProperty(automation, "gameProcessName", "watchProcessName");
        RenameProperty(automation, "gameExecutablePath", "launchExecutablePath");

        // v1에는 연동 스위치가 없었다. URL·실행 파일을 설정한 구성과, 복원 보류를
        // 끈 구성(v1은 이 경우에도 Discord 실행·전면 표시를 수행했다)은 연동을 켜서
        // 기존 동작을 보존한다. 반면 복원 보류가 켜져 있는데 URL이 없는 구성은
        // v1에서 복원이 영구 대기에 빠지던 문제가 있으므로 연동을 꺼서 복구한다.
        if (automation["useDiscordIntegration"] is null)
        {
            var apiBaseUrl = (string?)automation["discordApiBaseUrl"];
            var discordExecutablePath = (string?)automation["discordExecutablePath"];
            var deferRestore =
                automation["deferRestoreWhileDiscordInVoice"] is not JsonValue defer ||
                !defer.TryGetValue<bool>(out var deferValue) ||
                deferValue;
            automation["useDiscordIntegration"] =
                !string.IsNullOrWhiteSpace(apiBaseUrl) ||
                !string.IsNullOrWhiteSpace(discordExecutablePath) ||
                !deferRestore;
        }
    }

    /// <summary>
    /// v2까지는 감시 프로세스가 종료되면 항상 원래 장치로 복원했다. v3부터는
    /// 현재 장치를 유지하는 것이 기본이므로, 옵션이 없는 파일은 새 기본값을
    /// 명시해 저장한다. 다른 설정값은 그대로 둔다.
    /// </summary>
    private static void MigrateAutomationToV3(JsonObject automation)
    {
        automation["restoreAudioOnExit"] ??= false;

        // v3부터는 Discord가 이미 실행 중이어도 창을 전면으로 가져오지 않는다.
        automation.Remove("bringDiscordToFront");
    }

    /// <summary>
    /// v4는 Discord 음성채널 자동 입장을 추가합니다. 새 기능이므로 기본값은 꺼짐이며,
    /// 기존 설정값은 하나도 건드리지 않습니다. RPC 토큰과 client secret은 이 파일이
    /// 아니라 DPAPI 저장소에만 기록합니다.
    /// </summary>
    private static void MigrateAutomationToV4(JsonObject automation)
    {
        automation["autoJoinVoiceChannel"] ??= false;
        automation["voiceChannelTarget"] ??= string.Empty;
        automation["discordRpcClientId"] ??= string.Empty;
    }

    private static void RenameProperty(JsonObject target, string oldName, string newName)
    {
        if (target[newName] is null && target[oldName] is not null)
        {
            target[newName] = target[oldName]!.DeepClone();
        }
        target.Remove(oldName);
    }
}
