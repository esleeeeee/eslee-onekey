using System.Threading;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace Eslee.OneKey.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "Local\\eslee.OneKey.SingleInstance", out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show(
                "eslee OneKey가 이미 실행 중입니다. 트레이 아이콘을 확인하세요.",
                "eslee OneKey",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var startMinimized = e.Args.Any(argument =>
            argument.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(startMinimized);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstance?.ReleaseMutex();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
