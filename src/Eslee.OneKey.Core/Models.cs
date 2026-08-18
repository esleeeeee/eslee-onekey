namespace Eslee.OneKey.Core;

public enum AutomationState
{
    Idle,
    Starting,
    Active,
    RestorePending,
    Restoring,
    Completed,
    Failed,
}

public enum AutomationTrigger
{
    Hotkey,
    ProcessStarted,
}

public enum DiscordVoiceState
{
    NotInVoice,
    InVoice,
    Unavailable,
    Unauthorized,
    NotReady,
}

public sealed record DiscordVoiceCheck(DiscordVoiceState State, string? Error = null);

public sealed record AudioEndpoint(string Id, string Name, bool IsActive);

public sealed record HotkeyGesture(
    bool Control = true,
    bool Alt = true,
    bool Shift = false,
    bool Windows = false,
    string Key = "V")
{
    public override string ToString()
    {
        var modifiers = new List<string>();
        if (Control) modifiers.Add("Ctrl");
        if (Alt) modifiers.Add("Alt");
        if (Shift) modifiers.Add("Shift");
        if (Windows) modifiers.Add("Win");
        modifiers.Add(Key.ToUpperInvariant());
        return string.Join(" + ", modifiers);
    }
}

public sealed record AutomationSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "새 자동화";
    public bool Enabled { get; init; } = true;
    public HotkeyGesture Hotkey { get; init; } = new();
    public string WatchProcessName { get; init; } = string.Empty;
    public string LaunchExecutablePath { get; init; } = string.Empty;
    public bool UseDiscordIntegration { get; init; }
    public string DiscordProcessName { get; init; } = "Discord";
    public string DiscordExecutablePath { get; init; } = string.Empty;
    public string TargetAudioEndpointId { get; init; } = string.Empty;
    public string DiscordApiBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// 감시 프로세스가 종료된 뒤 원래 오디오 장치로 자동 복원할지 여부입니다.
    /// 기본값은 현재 장치 유지이며, 켰을 때만 복원과 복원 대기가 동작합니다.
    /// </summary>
    public bool RestoreAudioOnExit { get; init; }

    /// <summary>
    /// 자동화 시작 시 지정한 Discord 음성채널에 자동 입장할지 여부입니다.
    /// Discord 연동과 별개로 켜야 하며 기본값은 꺼짐입니다.
    /// </summary>
    public bool AutoJoinVoiceChannel { get; init; }

    /// <summary>음성채널 링크 또는 Channel ID입니다. 비밀값이 아닙니다.</summary>
    public string VoiceChannelTarget { get; init; } = string.Empty;

    /// <summary>RPC용 Discord 애플리케이션 Client ID입니다. 공개 값이라 설정에 보관합니다.</summary>
    public string DiscordRpcClientId { get; init; } = string.Empty;

    public bool DeferRestoreWhileDiscordInVoice { get; init; } = true;
    public TimeSpan ProcessPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RestorePollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 4;
    public bool StartWithWindows { get; init; }
    public List<AutomationSettings> Automations { get; init; } = [new()];
}

public sealed record AutomationSession
{
    public AutomationState State { get; init; }
    public string? OriginalAudioEndpointId { get; init; }
    public string? ManagedAudioEndpointId { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
}

public sealed record AutomationStartResult(bool Started, string? Reason = null)
{
    public static AutomationStartResult Ignored(string reason) => new(false, reason);
    public static AutomationStartResult Success() => new(true);
}

public sealed record HotkeyRegistrationResult(bool Succeeded, string? Error = null);
