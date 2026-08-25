using System.ComponentModel;
using System.Runtime.CompilerServices;
using Eslee.OneKey.Core;

namespace Eslee.OneKey.App;

/// <summary>
/// 왼쪽 목록에 보이는 자동화 한 줄입니다. 이름, 단축키, 지금 상태만 담습니다.
/// </summary>
public sealed class AutomationRuleViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _hotkeyText = string.Empty;
    private string _stateText = string.Empty;
    private bool _enabled = true;

    public AutomationRuleViewModel(AutomationSettings rule)
    {
        Id = rule.Id;
        _name = string.IsNullOrWhiteSpace(rule.Name) ? "이름 없는 자동화" : rule.Name;
        _hotkeyText = string.IsNullOrWhiteSpace(rule.Hotkey.Key) ? "단축키 없음" : rule.Hotkey.ToString();
        _enabled = rule.Enabled;
    }

    public Guid Id { get; }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string HotkeyText { get => _hotkeyText; set => Set(ref _hotkeyText, value); }
    public string StateText { get => _stateText; set => Set(ref _stateText, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

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
