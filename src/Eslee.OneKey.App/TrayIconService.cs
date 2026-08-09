using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Eslee.OneKey.App;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pauseItem;

    public TrayIconService(MainWindow window)
    {
        _pauseItem = new Forms.ToolStripMenuItem("자동화 일시정지");
        _pauseItem.Click += (_, _) => window.TogglePausedFromTray();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("OneKey 열기", null, (_, _) => window.OpenFromTray());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("현재 상태 확인", null, (_, _) => window.ShowCurrentStatus());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => window.ExitApplication());

        _icon = new Forms.NotifyIcon
        {
            Text = "eslee OneKey",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => window.OpenFromTray();
    }

    private bool _paused;
    private bool _restorePending;

    /// <summary>파이프 메뉴의 체크 표시에 쓰는 현재 일시정지 상태입니다.</summary>
    public bool IsPaused => _paused;

    /// <summary>
    /// Tray Folder Hosted 모드 전환용 아이콘 표시 제어입니다. 자동화 동작은 그대로
    /// 유지되며 아이콘만 숨겨집니다. UI 스레드에서 호출해야 합니다.
    /// </summary>
    public void SetTrayIconVisible(bool visible) => _icon.Visible = visible;

    public void SetPaused(bool paused)
    {
        _paused = paused;
        _pauseItem.Checked = paused;
        UpdateText();
    }

    public void SetRestorePending(bool pending)
    {
        _restorePending = pending;
        UpdateText();
    }

    private void UpdateText() => _icon.Text =
        _paused ? "eslee OneKey (일시정지)"
        : _restorePending ? "eslee OneKey (복원 대기)"
        : "eslee OneKey";

    public void ShowBalloon(string title, string message)
    {
        if (!_icon.Visible)
        {
            // Hosted 모드에서는 아이콘이 없어 풍선을 표시할 수 없습니다.
            return;
        }

        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
