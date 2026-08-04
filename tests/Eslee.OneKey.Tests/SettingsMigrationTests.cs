using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// v1(게임 중심 속성명) settings.json이 값 손실 없이 v2 범용 모델로
/// 로드되는지 검증한다. 기존 파일을 읽지 못해 설정이 초기화되면 안 된다.
/// </summary>
public sealed class SettingsMigrationTests
{
    private const string V1SettingsJson = """
        {
          "schemaVersion": 1,
          "startWithWindows": true,
          "automations": [
            {
              "id": "8d2e7e19-e3e6-4464-a2e2-ca4b6102a75b",
              "name": "라이엇 클라이언트",
              "enabled": true,
              "hotkey": {
                "control": true,
                "alt": true,
                "shift": true,
                "windows": false,
                "key": "V"
              },
              "gameProcessName": "VALORANT",
              "gameExecutablePath": "C:\\Riot Games\\Riot Client\\RiotClientServices.exe",
              "discordProcessName": "Discord",
              "discordExecutablePath": "",
              "bringDiscordToFront": true,
              "targetAudioEndpointId": "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}",
              "discordApiBaseUrl": "https://bot.example.invalid",
              "deferRestoreWhileDiscordInVoice": true,
              "processPollInterval": "00:00:01",
              "restorePollInterval": "00:00:05"
            }
          ]
        }
        """;

    private static async Task<T> WithStoreAsync<T>(string json, Func<JsonSettingsStore, Task<T>> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "onekey-tests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        paths.EnsureDirectories();
        try
        {
            await File.WriteAllTextAsync(paths.SettingsFile, json);
            return await action(new JsonSettingsStore(paths));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task V1SettingsLoadIntoGeneralizedModelWithoutLoss()
    {
        var settings = await WithStoreAsync(
            V1SettingsJson,
            store => store.LoadAsync(CancellationToken.None));

        Assert.Equal(SettingsMigration.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(settings.StartWithWindows);

        var automation = Assert.Single(settings.Automations);
        Assert.Equal(Guid.Parse("8d2e7e19-e3e6-4464-a2e2-ca4b6102a75b"), automation.Id);
        Assert.Equal("라이엇 클라이언트", automation.Name);
        Assert.True(automation.Enabled);
        Assert.Equal(new HotkeyGesture(true, true, true, false, "V"), automation.Hotkey);
        Assert.Equal("VALORANT", automation.WatchProcessName);
        Assert.Equal(@"C:\Riot Games\Riot Client\RiotClientServices.exe", automation.LaunchExecutablePath);
        Assert.Equal("Discord", automation.DiscordProcessName);
        Assert.Equal(
            "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}",
            automation.TargetAudioEndpointId);
        Assert.Equal("https://bot.example.invalid", automation.DiscordApiBaseUrl);
        Assert.True(automation.DeferRestoreWhileDiscordInVoice);
        Assert.Equal(TimeSpan.FromSeconds(1), automation.ProcessPollInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), automation.RestorePollInterval);
    }

    [Fact]
    public async Task V1SettingsWithDiscordConfigEnableIntegration()
    {
        var settings = await WithStoreAsync(
            V1SettingsJson,
            store => store.LoadAsync(CancellationToken.None));

        Assert.True(Assert.Single(settings.Automations).UseDiscordIntegration);
    }

    [Fact]
    public async Task V1SettingsWithDeferAndWithoutDiscordConfigDisableIntegration()
    {
        // v1에서 복원 보류가 켜져 있는데 URL이 없으면 복원이 영구 대기에 빠졌다.
        // 이 구성은 연동을 꺼서 복구한다.
        var json = V1SettingsJson
            .Replace("https://bot.example.invalid", string.Empty);
        var settings = await WithStoreAsync(json, store => store.LoadAsync(CancellationToken.None));

        Assert.False(Assert.Single(settings.Automations).UseDiscordIntegration);
    }

    [Fact]
    public async Task V1SettingsWithDeferDisabledKeepDiscordIntegrationOn()
    {
        // v1은 URL 없이도 Discord 실행·전면 표시를 수행했다. 복원 보류를 끈
        // 구성은 그 동작이 유지되도록 연동을 켠 채 마이그레이션한다.
        var json = V1SettingsJson
            .Replace("https://bot.example.invalid", string.Empty)
            .Replace("\"deferRestoreWhileDiscordInVoice\": true", "\"deferRestoreWhileDiscordInVoice\": false");
        var settings = await WithStoreAsync(json, store => store.LoadAsync(CancellationToken.None));

        Assert.True(Assert.Single(settings.Automations).UseDiscordIntegration);
    }

    [Fact]
    public async Task NonObjectJsonRootThrowsInsteadOfSilentlyResettingSettings()
    {
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            WithStoreAsync("[]", store => store.LoadAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task V2SettingsRoundTripWithoutLegacyPropertyNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "onekey-tests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        var store = new JsonSettingsStore(paths);
        var original = new AppSettings
        {
            StartWithWindows = true,
            Automations =
            [
                new AutomationSettings
                {
                    Name = "업무 모드",
                    WatchProcessName = "obs64",
                    LaunchExecutablePath = @"C:\Tools\obs64.exe",
                    UseDiscordIntegration = false,
                    TargetAudioEndpointId = "endpoint-id",
                },
            ],
        };
        try
        {
            await store.SaveAsync(original, CancellationToken.None);
            var json = await File.ReadAllTextAsync(paths.SettingsFile);
            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.Contains("watchProcessName", json);
            Assert.Contains("launchExecutablePath", json);
            Assert.DoesNotContain("gameProcessName", json);
            Assert.DoesNotContain("gameExecutablePath", json);
            Assert.Equal(original.SchemaVersion, loaded.SchemaVersion);
            Assert.Equal(original.StartWithWindows, loaded.StartWithWindows);
            Assert.Equal(original.Automations, loaded.Automations);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
