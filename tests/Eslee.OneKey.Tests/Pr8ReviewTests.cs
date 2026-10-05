using Eslee.OneKey.Core;

namespace Eslee.OneKey.Tests;

public sealed class Pr8ReviewTests
{
    [Fact]
    public async Task ForcedAccountSwitchDoesNotCompleteOnTheOldGameExitWhileNewGameStarts()
    {
        var first = new GameAccountProfile();
        var second = new GameAccountProfile { CloseRunningGameToSwitch = true };
        var rule = new AutomationSettings
        {
            AccountProfileId = first.Id,
            WatchProcessName = "game",
            LaunchExecutablePath = "launcher.exe",
            LaunchArguments = "--start-game",
            TargetAudioEndpointId = "headset",
            RestoreAudioOnExit = true,
        };
        var audio = new FakeAudioService();
        var processes = new FakeProcessService();
        var sessions = new FakeGameSessionService();
        await using var engine = new AutomationEngine(rule, audio, processes,
            new FakeVoiceClient([]), new FakeSessionStore(), new FakeClock(), new FakeLogger(),
            accountSessions: sessions);
        await engine.StartRuleAsync(rule, first);

        // The forced switch closed the old game. Only the launcher exists while
        // its launch arguments start the new game; the monitor queued the old exit.
        await engine.StartRuleAsync(rule with { AccountProfileId = second.Id }, second);
        await engine.OnWatchedProcessExitedAsync();

        Assert.Equal(AutomationState.Active, engine.State);
        Assert.Equal("headset", audio.DefaultId);

        // Once the replacement game has actually started, its own exit must
        // restore audio normally, even within the restart grace period.
        processes.Running.Add("game");
        await engine.StartAsync(AutomationTrigger.ProcessStarted);
        processes.Running.Remove("game");
        await engine.OnWatchedProcessExitedAsync();
        Assert.Equal(AutomationState.Completed, engine.State);
        Assert.Equal("speaker", audio.DefaultId);
    }

    [Fact]
    public async Task DisabledAudioDoesNotWaitForDiscordWhenThereIsNothingToRestore()
    {
        var rule = new AutomationSettings
        {
            WatchProcessName = "game",
            LaunchExecutablePath = "launcher.exe",
            TargetAudioEndpointId = "headset",
            RestoreAudioOnExit = true,
            UseDiscordIntegration = true,
            DeferRestoreWhileDiscordInVoice = true,
        };
        var voice = new FakeVoiceClient([DiscordVoiceState.InVoice]);
        await using var engine = new AutomationEngine(rule, new FakeAudioService(),
            new FakeProcessService(), voice, new FakeSessionStore(), new FakeClock(), new FakeLogger());
        engine.AudioSwitchingEnabled = false;
        await engine.StartAsync(AutomationTrigger.Hotkey);
        await engine.OnWatchedProcessExitedAsync();

        Assert.Equal(AutomationState.Completed, engine.State);
        Assert.Equal(0, voice.Calls);
    }

    [Fact]
    public async Task MissingAudioDoesNotWaitForDiscordWhenThereIsNothingToRestore()
    {
        var rule = new AutomationSettings
        {
            WatchProcessName = "game",
            LaunchExecutablePath = "launcher.exe",
            TargetAudioEndpointId = "missing",
            RestoreAudioOnExit = true,
            UseDiscordIntegration = true,
            DeferRestoreWhileDiscordInVoice = true,
        };
        var voice = new FakeVoiceClient([DiscordVoiceState.InVoice]);
        await using var engine = new AutomationEngine(rule, new FakeAudioService(),
            new FakeProcessService(), voice, new FakeSessionStore(), new FakeClock(), new FakeLogger());
        await engine.StartAsync(AutomationTrigger.Hotkey);
        await engine.OnWatchedProcessExitedAsync();
        Assert.Equal(AutomationState.Completed, engine.State);
        Assert.Equal(0, voice.Calls);
    }
}
