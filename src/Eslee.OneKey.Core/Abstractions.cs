namespace Eslee.OneKey.Core;

public interface IAudioEndpointService
{
    Task<IReadOnlyList<AudioEndpoint>> GetOutputEndpointsAsync(CancellationToken cancellationToken);
    Task<string?> GetDefaultOutputIdAsync(CancellationToken cancellationToken);
    Task SetDefaultOutputAsync(string endpointId, CancellationToken cancellationToken);
}

public interface IProcessService
{
    Task<bool> IsRunningAsync(string processName, CancellationToken cancellationToken);
    Task StartAsync(string executablePath, CancellationToken cancellationToken);
    Task<bool> BringToFrontAsync(string processName, CancellationToken cancellationToken);
}

public interface IDiscordVoiceStatusClient
{
    Task<DiscordVoiceCheck> CheckAsync(CancellationToken cancellationToken);
}

public interface ISessionStore
{
    Task<AutomationSession?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AutomationSession session, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}

public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IAppLogger
{
    void Info(string eventName, string message);
    void Warning(string eventName, string message);
    void Error(string eventName, Exception exception, string message);
}

public interface IHotkeyService : IAsyncDisposable
{
    event Func<Task>? Pressed;
    Task<HotkeyRegistrationResult> RegisterAsync(
        HotkeyGesture gesture,
        CancellationToken cancellationToken);
    void Unregister();
}

public interface IProcessMonitor : IAsyncDisposable
{
    event Func<Task>? ProcessStarted;
    event Func<Task>? ProcessExited;
    Task StartAsync(string processName, TimeSpan interval, CancellationToken cancellationToken);
    Task StopAsync();
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public interface ISecretStore
{
    Task<string?> LoadDiscordApiTokenAsync(CancellationToken cancellationToken);
    Task SaveDiscordApiTokenAsync(string token, CancellationToken cancellationToken);
    Task ClearDiscordApiTokenAsync(CancellationToken cancellationToken);
}

public interface IStartupRegistrationService
{
    bool IsEnabled();
    void SetEnabled(bool enabled, string executablePath);
}
