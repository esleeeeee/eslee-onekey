using System.Text.Json;
using System.Text.Json.Nodes;
using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// v5까지는 Discord 연결 값이 자동화마다 따로 있었습니다. 자동화를 바꿀 때마다 다른
/// 값이 보여서, 앱 전체에 하나만 두도록 끌어올립니다. 기존 값은 잃지 않습니다.
/// </summary>
public sealed class SettingsMigrationV6Tests
{
    private const string V5Json = """
        {
          "schemaVersion": 5,
          "startWithWindows": true,
          "automations": [
            {
              "id": "8d2e7e19-e3e6-4464-a2e2-ca4b6102a75b",
              "name": "한섭",
              "enabled": true,
              "hotkey": { "control": true, "alt": true, "shift": true, "windows": false, "key": "V" },
              "discordRpcClientId": "1525689872621240442",
              "discordApiBaseUrl": "https://bot.example",
              "discordExecutablePath": "C:/Discord/Discord.exe",
              "discordProcessName": "Discord",
              "voiceChannelTarget": "1111111111111111111",
              "voiceChannelGuildId": "2222222222222222222"
            },
            {
              "id": "9a1e7e19-e3e6-4464-a2e2-ca4b6102a75c",
              "name": "아섭",
              "enabled": true,
              "hotkey": { "control": true, "alt": true, "shift": true, "windows": false, "key": "A" },
              "discordRpcClientId": "1525689872621240442",
              "discordApiBaseUrl": "https://bot.example",
              "discordExecutablePath": "C:/Discord/Discord.exe",
              "discordProcessName": "Discord",
              "voiceChannelTarget": "3333333333333333333",
              "voiceChannelGuildId": "4444444444444444444"
            }
          ],
          "accountProfiles": []
        }
        """;

    private static AppSettings Migrate(string json) =>
        JsonSerializer.Deserialize<AppSettings>(
            SettingsMigration.Migrate(JsonNode.Parse(json)).ToJsonString(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

    [Fact]
    public void TheDiscordConnectionValuesMoveToTheAppLevel()
    {
        var settings = Migrate(V5Json);

        Assert.Equal(SettingsMigration.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("1525689872621240442", settings.DiscordRpcClientId);
        Assert.Equal("https://bot.example", settings.DiscordApiBaseUrl);
        Assert.Equal("C:/Discord/Discord.exe", settings.DiscordExecutablePath);
        Assert.Equal("Discord", settings.DiscordProcessName);
    }

    [Fact]
    public void EachAutomationKeepsItsOwnServerAndVoiceChannel()
    {
        var settings = Migrate(V5Json);

        Assert.Equal(
            ["1111111111111111111", "3333333333333333333"],
            settings.Automations.Select(rule => rule.VoiceChannelTarget));
        Assert.Equal(
            ["2222222222222222222", "4444444444444444444"],
            settings.Automations.Select(rule => rule.VoiceChannelGuildId));
    }

    [Fact]
    public void AValueOnlyTheSecondAutomationHasIsStillPickedUp()
    {
        var node = JsonNode.Parse(V5Json)!;
        var automations = (JsonArray)node["automations"]!;
        ((JsonObject)automations[0]!)["discordApiBaseUrl"] = "";

        var settings = Migrate(node.ToJsonString());

        Assert.Equal("https://bot.example", settings.DiscordApiBaseUrl);
    }

    [Fact]
    public void MigratingTwiceChangesNothing()
    {
        var once = SettingsMigration.Migrate(JsonNode.Parse(V5Json)).ToJsonString();
        var twice = SettingsMigration.Migrate(JsonNode.Parse(once)).ToJsonString();

        Assert.Equal(once, twice);
    }
}
