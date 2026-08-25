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

/// <summary>사용자가 가입한 Discord 서버입니다. 이름은 로컬 Discord가 알려 줍니다.</summary>
public sealed record DiscordGuild(string Id, string Name);

/// <summary>사용자에게 보이는 음성채널입니다.</summary>
public sealed record DiscordVoiceChannel(string Id, string Name);

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
    /// <summary>규칙을 구분하는 값입니다. 이름을 바꿔도 그대로 유지됩니다.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "새 자동화";
    public bool Enabled { get; init; } = true;
    public HotkeyGesture Hotkey { get; init; } = new();

    /// <summary>
    /// 이 규칙으로 시작할 때 활성화할 계정 프로필입니다. 비워 두면 계정을 건드리지
    /// 않습니다. 단축키는 규칙에만 있고 계정 프로필에는 없습니다.
    /// </summary>
    public Guid? AccountProfileId { get; init; }
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

    /// <summary>
    /// 음성채널을 고른 서버의 ID입니다. 목록을 다시 열 때 어느 서버를 보고 있었는지
    /// 기억하는 용도이며, 비밀값이 아닙니다.
    /// </summary>
    public string VoiceChannelGuildId { get; init; } = string.Empty;

    /// <summary>RPC용 Discord 애플리케이션 Client ID입니다. 공개 값이라 설정에 보관합니다.</summary>
    public string DiscordRpcClientId { get; init; } = string.Empty;

    public bool DeferRestoreWhileDiscordInVoice { get; init; } = true;
    public TimeSpan ProcessPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RestorePollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 6;
    public bool StartWithWindows { get; init; }

    /// <summary>
    /// Discord 연결에 쓰는 값들입니다. 자동화마다 다를 이유가 없어 앱 전체에서
    /// 하나만 둡니다. 어느 자동화를 보고 있든 같은 값이어야 합니다.
    /// API Token은 여기 두지 않고 DPAPI에만 보관합니다.
    /// </summary>
    public string DiscordRpcClientId { get; init; } = string.Empty;
    public string DiscordApiBaseUrl { get; init; } = string.Empty;
    public string DiscordExecutablePath { get; init; } = string.Empty;
    public string DiscordProcessName { get; init; } = "Discord";
    public List<AutomationSettings> Automations { get; init; } = [new()];

    /// <summary>
    /// 계정 프로필은 자동화와 분리해 둡니다. 자동화 규칙이 늘어나도 같은 프로필을
    /// 재사용할 수 있고, 특정 게임에 묶이지 않습니다.
    /// </summary>
    public List<GameAccountProfile> AccountProfiles { get; init; } = [];
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
