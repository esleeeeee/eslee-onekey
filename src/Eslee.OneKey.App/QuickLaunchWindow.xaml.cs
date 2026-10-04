using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Eslee.OneKey.Core;
using Button = System.Windows.Controls.Button;

namespace Eslee.OneKey.App;

/// <summary>
/// 바탕화면에 떠 있는 작은 버튼 창입니다. 켜 둔 자동화마다 버튼이 하나씩 있고, 누르면
/// 그 자동화의 단축키를 누른 것과 같습니다. 실행 자체는 MainWindow가 맡고, 이 창은
/// 무엇이 눌렸는지만 알립니다.
/// </summary>
public partial class QuickLaunchWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;

    public QuickLaunchWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => HideFromTaskSwitcher();
    }

    /// <summary>버튼이 눌렸습니다. 끝날 때까지 그 버튼은 다시 눌리지 않습니다.</summary>
    public event Func<Guid, Task>? RuleRequested;

    /// <summary>사용자가 창을 끌어 옮겼거나 항상 위 설정을 바꿨습니다.</summary>
    public event EventHandler? PlacementChanged;
    public event EventHandler? HideRequested;
    public event EventHandler? OpenAppRequested;

    public void SetRules(IEnumerable<AutomationSettings> rules)
    {
        var items = rules
            .Where(rule => rule.Enabled)
            .Select(rule => new QuickButtonItem(rule.Id, rule.Name, rule.Hotkey.ToString()))
            .ToArray();
        RuleButtons.ItemsSource = items;
        EmptyText.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetTopmost(bool topmost)
    {
        Topmost = topmost;
        TopmostMenuItem.IsChecked = topmost;
    }

    public void SetStatus(string? text, bool isError)
    {
        StatusText.Text = text ?? string.Empty;
        StatusText.Foreground = new SolidColorBrush(isError
            ? System.Windows.Media.Color.FromRgb(0xFF, 0x9A, 0x9A)
            : System.Windows.Media.Color.FromRgb(0xB9, 0xB5, 0xD6));
        StatusText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 저장해 둔 자리가 지금 화면 안에 있으면 거기에, 아니면 화면 오른쪽 아래에 둡니다.
    /// 모니터 구성이 바뀌어 창이 화면 밖으로 사라지는 일을 막습니다.
    /// </summary>
    public void Place(double? left, double? top)
    {
        UpdateLayout();
        var width = ActualWidth > 0 ? ActualWidth : 180;
        var height = ActualHeight > 0 ? ActualHeight : 120;
        var visible = left is { } x && top is { } y &&
            x >= SystemParameters.VirtualScreenLeft &&
            y >= SystemParameters.VirtualScreenTop &&
            x + width <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            y + height <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (visible)
        {
            Left = left!.Value;
            Top = top!.Value;
            return;
        }

        var area = SystemParameters.WorkArea;
        Left = area.Right - width - 24;
        Top = area.Bottom - height - 24;
    }

    private async void Rule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id } button || RuleRequested is not { } handler)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await handler(id);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
        PlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostMenuItem.IsChecked;
        PlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenApp_Click(object sender, RoutedEventArgs e) =>
        OpenAppRequested?.Invoke(this, EventArgs.Empty);

    private void Hide_Click(object sender, RoutedEventArgs e) =>
        HideRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Alt+Tab 목록에 끼지 않게 합니다. 작업 창이 아니라 붙박이 버튼입니다.</summary>
    private void HideFromTaskSwitcher()
    {
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(handle, GwlExStyle, GetWindowLongPtr(handle, GwlExStyle) | WsExToolWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    public sealed record QuickButtonItem(Guid Id, string Name, string HotkeyText);
}
