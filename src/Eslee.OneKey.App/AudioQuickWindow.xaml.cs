using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace Eslee.OneKey.App;

/// <summary>
/// 자동화 버튼 창과 따로 떠 있는 오디오 버튼 창입니다. 실행할 때 출력 장치를 바꿀지를
/// 바로 켜고 끄고, 볼륨 믹서를 엽니다. 실제 처리는 MainWindow가 맡습니다.
/// </summary>
public partial class AudioQuickWindow : Window
{
    private static readonly SolidColorBrush OnBrush = new(Color.FromRgb(0x6D, 0x5D, 0xFB));
    private static readonly SolidColorBrush OffBrush = new(Color.FromRgb(0x3D, 0x3A, 0x57));

    public AudioQuickWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => QuickLaunchWindow.HideFromTaskSwitcher(this);
    }

    public event EventHandler? AutoSwitchToggleRequested;
    public event EventHandler? VolumeMixerRequested;

    /// <summary>헤드셋 버튼이면 true, 스피커 버튼이면 false입니다.</summary>
    public event Action<bool>? DeviceRequested;

    public void SetAutoSwitch(bool enabled) =>
        AutoSwitchStateText.Text = enabled ? "실행 시 자동 전환 · 켜짐" : "실행 시 자동 전환 · 꺼짐";

    /// <summary>지금 기본 출력인 쪽을 강조합니다. 둘 다 아니면 null입니다.</summary>
    public void SetActiveDevice(bool? headset)
    {
        SpeakerButton.Background = headset == false ? OnBrush : OffBrush;
        HeadsetButton.Background = headset == true ? OnBrush : OffBrush;
    }

    private void Speaker_Click(object sender, RoutedEventArgs e) => DeviceRequested?.Invoke(false);

    private void Headset_Click(object sender, RoutedEventArgs e) => DeviceRequested?.Invoke(true);

    private void AutoSwitch_Click(object sender, RoutedEventArgs e) =>
        AutoSwitchToggleRequested?.Invoke(this, EventArgs.Empty);

    private void VolumeMixer_Click(object sender, RoutedEventArgs e) =>
        VolumeMixerRequested?.Invoke(this, EventArgs.Empty);

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
