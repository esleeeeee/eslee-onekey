using System.ComponentModel;
using System.Runtime.CompilerServices;
using Eslee.OneKey.Core;

namespace Eslee.OneKey.App;

/// <summary>
/// 설정 화면의 계정 프로필 한 줄입니다. 아이디나 비밀번호는 다루지 않고,
/// 저장된 로그인 세션의 등록 상태만 보여 줍니다.
/// </summary>
public sealed class AccountProfileViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _sessionFilePath = string.Empty;
    private string _launcherProcesses = string.Empty;
    private string _blockingProcesses = string.Empty;
    private string _status = "확인 중...";

    public AccountProfileViewModel()
    {
    }

    public AccountProfileViewModel(GameAccountProfile profile)
    {
        Id = profile.Id;
        _name = profile.Name;
        _sessionFilePath = profile.SessionFilePath;
        _launcherProcesses = string.Join(", ", profile.LauncherProcessNames);
        _blockingProcesses = string.Join(", ", profile.BlockingProcessNames);
    }

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get => _name; set => Set(ref _name, value); }
    public string SessionFilePath { get => _sessionFilePath; set => Set(ref _sessionFilePath, value); }
    public string LauncherProcesses { get => _launcherProcesses; set => Set(ref _launcherProcesses, value); }
    public string BlockingProcesses { get => _blockingProcesses; set => Set(ref _blockingProcesses, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    public GameAccountProfile ToProfile() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        SessionFilePath = SessionFilePath.Trim(),
        LauncherProcessNames = SplitNames(LauncherProcesses),
        BlockingProcessNames = SplitNames(BlockingProcesses),
    };

    private static List<string> SplitNames(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    public static string Describe(GameAccountProfileStatus status) => status switch
    {
        GameAccountProfileStatus.Enrolled => "등록됨",
        GameAccountProfileStatus.NeedsReenrollment => "재등록 필요",
        _ => "미등록",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
