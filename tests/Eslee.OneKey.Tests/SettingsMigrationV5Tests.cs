using System.Text.Json;
using System.Text.Json.Nodes;
using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// v4까지는 계정 프로필도 단축키를 들고 있었습니다. 같은 조합이 두 번 등록되면
/// Windows가 어느 쪽이 눌렸는지 구분하지 못하므로 단축키를 자동화 규칙으로 모읍니다.
/// 이때 기존 실행 설정과 계정 프로필 id는 하나도 잃으면 안 됩니다.
/// </summary>
public sealed class SettingsMigrationV5Tests
{
    private const string V4SettingsJson = """
        {
          "schemaVersion": 4,
          "startWithWindows": true,
          "automations": [
            {
              "id": "8d2e7e19-e3e6-4464-a2e2-ca4b6102a75b",
              "name": "라이엇 클라이언트",
              "enabled": true,
              "hotkey": { "control": true, "alt": true, "shift": true, "windows": false, "key": "V" },
              "watchProcessName": "VALORANT-Win64-Shipping",
              "launchExecutablePath": "C:/Riot Games/Riot Client/RiotClientServices.exe",
              "useDiscordIntegration": false,
              "discordProcessName": "Discord",
              "targetAudioEndpointId": "{0.0.0.00000000}.{headset}",
              "restoreAudioOnExit": false,
              "autoJoinVoiceChannel": true,
              "voiceChannelTarget": "2223334445556667778",
              "discordRpcClientId": "1525689872621",
              "deferRestoreWhileDiscordInVoice": true
            }
          ],
          "accountProfiles": [
            {
              "id": "aaaaaaaa-0000-0000-0000-000000000001",
              "name": "한섭",
              "hotkey": { "control": true, "alt": true, "shift": true, "windows": false, "key": "V" },
              "sessionFilePath": "C:/launcher/session.yaml",
              "launcherProcessNames": ["RiotClientUx"],
              "blockingProcessNames": ["VALORANT-Win64-Shipping"]
            },
            {
              "id": "bbbbbbbb-0000-0000-0000-000000000002",
              "name": "아섭",
              "hotkey": { "control": true, "alt": true, "shift": true, "windows": false, "key": "A" },
              "sessionFilePath": "C:/launcher/session.yaml",
              "launcherProcessNames": ["RiotClientUx"],
              "blockingProcessNames": ["VALORANT-Win64-Shipping"]
            }
          ]
        }
        """;

    private static AppSettings Migrate(string json) =>
        JsonSerializer.Deserialize<AppSettings>(
            SettingsMigration.Migrate(JsonNode.Parse(json)).ToJsonString(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

    [Fact]
    public void EachAccountHotkeyBecomesItsOwnRule()
    {
        var settings = Migrate(V4SettingsJson);

        Assert.Equal(SettingsMigration.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(2, settings.Automations.Count);

        var korea = settings.Automations[0];
        var asia = settings.Automations[1];
        Assert.Equal("V", korea.Hotkey.Key);
        Assert.Equal("A", asia.Hotkey.Key);
        Assert.Equal(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), korea.AccountProfileId);
        Assert.Equal(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), asia.AccountProfileId);
        Assert.NotEqual(korea.Id, asia.Id);
    }

    [Fact]
    public void TheAccountProfilesKeepTheirIdentityAndLoseTheirHotkeys()
    {
        var settings = Migrate(V4SettingsJson);

        // id가 그대로여야 DPAPI에 저장된 세션을 다시 등록하지 않고 계속 쓸 수 있다.
        Assert.Equal(
            [
                Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
                Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
            ],
            settings.AccountProfiles.Select(profile => profile.Id));
        Assert.Equal(["한섭", "아섭"], settings.AccountProfiles.Select(profile => profile.Name));
        Assert.All(settings.AccountProfiles, profile =>
            Assert.Equal(@"C:/launcher/session.yaml", profile.SessionFilePath));
    }

    [Fact]
    public void EveryRuleKeepsTheOriginalExecutionSettings()
    {
        var settings = Migrate(V4SettingsJson);

        Assert.All(settings.Automations, rule =>
        {
            Assert.Equal("VALORANT-Win64-Shipping", rule.WatchProcessName);
            Assert.Equal(@"C:/Riot Games/Riot Client/RiotClientServices.exe", rule.LaunchExecutablePath);
            Assert.Equal("{0.0.0.00000000}.{headset}", rule.TargetAudioEndpointId);
            Assert.False(rule.RestoreAudioOnExit);
            Assert.True(rule.AutoJoinVoiceChannel);
            Assert.Equal("2223334445556667778", rule.VoiceChannelTarget);
            Assert.Equal("1525689872621", rule.DiscordRpcClientId);
            Assert.True(rule.DeferRestoreWhileDiscordInVoice);
        });
    }

    [Fact]
    public void SettingsWithoutAccountProfilesKeepTheirSingleRule()
    {
        var node = JsonNode.Parse(V4SettingsJson)!;
        node["accountProfiles"] = new JsonArray();

        var settings = Migrate(node.ToJsonString());

        Assert.Single(settings.Automations);
        Assert.Equal("라이엇 클라이언트", settings.Automations[0].Name);
        Assert.Equal("V", settings.Automations[0].Hotkey.Key);
        Assert.Null(settings.Automations[0].AccountProfileId);
        Assert.NotEqual(Guid.Empty, settings.Automations[0].Id);
    }

    [Fact]
    public void MigratingTwiceChangesNothing()
    {
        var once = SettingsMigration.Migrate(JsonNode.Parse(V4SettingsJson)).ToJsonString();
        var twice = SettingsMigration.Migrate(JsonNode.Parse(once)).ToJsonString();

        Assert.Equal(once, twice);
    }
}
