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

    public void SetPaused(bool paused)
    {
        _pauseItem.Checked = paused;
        _icon.Text = paused ? "eslee OneKey (일시정지)" : "eslee OneKey";
    }

    public void SetRestorePending(bool pending)
    {
        if (pending)
        {
            _icon.Text = "eslee OneKey (Discord 통화 종료 대기)";
        }
    }

    public void ShowBalloon(string title, string message)
    {
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
