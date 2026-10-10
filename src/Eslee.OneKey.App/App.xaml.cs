using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace Eslee.OneKey.App;

public partial class App : System.Windows.Application
{
    private const string ShowWindowEventName = "Local\\eslee.OneKey.ShowWindow";
    // 설치 관리자가 업데이트·제거 전에 이 이벤트를 켜서 실행 중인 앱을 정상 종료시킨다.
    // 이름을 바꾸면 installer\eslee-onekey.iss도 함께 바꿔야 한다.
    private const string ShutdownEventName = "Local\\eslee.OneKey.Shutdown";
    private const int AllowAnyProcess = -1;

    private Mutex? _singleInstance;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _showWindowSignal;
    private RegisteredWaitHandle? _showWindowWait;
    private EventWaitHandle? _shutdownSignal;
    private RegisteredWaitHandle? _shutdownWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "Local\\eslee.OneKey.SingleInstance", out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            // 이미 트레이에 떠 있는데 다시 실행했다면 창을 열어 달라는 뜻이다.
            // 안내만 띄우고 끝내면 사용자는 트레이를 뒤져야 한다.
            if (!TryAskRunningInstanceToShow())
            {
                MessageBox.Show(
                    "eslee OneKey가 이미 실행 중입니다. 트레이 아이콘을 확인하세요.",
                    "eslee OneKey",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var startMinimized = e.Args.Any(argument =>
            argument.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(startMinimized);
        MainWindow = window;
        window.Show();
        ListenForShowRequests(window);
    }

    /// <summary>두 번째로 실행된 쪽이 보내는 신호를 받아 창을 엽니다.</summary>
    private void ListenForShowRequests(MainWindow window)
    {
        _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowWait = ThreadPool.RegisterWaitForSingleObject(
            _showWindowSignal,
            (_, _) => window.OpenFromTray(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        _shutdownSignal = new EventWaitHandle(false, EventResetMode.ManualReset, ShutdownEventName);
        _shutdownWait = ThreadPool.RegisterWaitForSingleObject(
            _shutdownSignal,
            (_, _) => window.Dispatcher.InvokeAsync(window.ExitApplication),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    private static bool TryAskRunningInstanceToShow()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var signal))
            {
                return false;
            }

            using (signal)
            {
                // 방금 사용자가 실행한 쪽만 창을 앞으로 가져올 권한이 있다. 그 권한을 넘긴다.
                AllowSetForegroundWindow(AllowAnyProcess);
                return signal.Set();
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWindowWait?.Unregister(null);
        _showWindowSignal?.Dispose();
        _shutdownWait?.Unregister(null);
        _shutdownSignal?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstance?.ReleaseMutex();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
