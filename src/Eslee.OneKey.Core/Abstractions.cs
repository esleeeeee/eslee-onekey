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

    /// <summary>이름이 같은 프로세스를 닫습니다. 이미 없으면 아무 일도 하지 않습니다.</summary>
    Task StopAsync(string processName, CancellationToken cancellationToken);
}

public interface IDiscordVoiceStatusClient
{
    Task<DiscordVoiceCheck> CheckAsync(CancellationToken cancellationToken);
}

public enum DiscordRpcStatus
{
    Connected,
    /// <summary>Discord 클라이언트가 없거나 준비되지 않았습니다.</summary>
    Unavailable,
    /// <summary>저장된 RPC 인증이 없거나 더 이상 유효하지 않습니다.</summary>
    NotAuthorized,
}

public sealed record DiscordRpcConnection(DiscordRpcStatus Status, string? Error = null);

/// <summary>
/// 로컬 Discord 클라이언트의 음성채널을 공식 RPC로 제어합니다. 통화 상태 확인용
/// <see cref="IDiscordVoiceStatusClient"/>(자체 봇 API)와는 별개의 경로입니다.
/// </summary>
public interface IDiscordVoiceChannelClient
{
    Task<DiscordRpcConnection> ConnectAsync(CancellationToken cancellationToken);
    Task<string?> GetSelectedVoiceChannelIdAsync(CancellationToken cancellationToken);
    Task SelectVoiceChannelAsync(string channelId, CancellationToken cancellationToken);
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
