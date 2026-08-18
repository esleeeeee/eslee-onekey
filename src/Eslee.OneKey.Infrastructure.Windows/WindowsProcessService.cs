using System.Diagnostics;
using System.Runtime.InteropServices;
using Eslee.OneKey.Core;

namespace Eslee.OneKey.Infrastructure.Windows;

public sealed class WindowsProcessService : IProcessService
{
    public Task<bool> IsRunningAsync(string processName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeProcessName(processName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Task.FromResult(false);
        }
        var processes = Process.GetProcessesByName(normalized);
        try
        {
            return Task.FromResult(processes.Length > 0);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public Task StartAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("실행 파일을 찾을 수 없습니다.", executablePath);
        }
        Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 종료할 때 상태 파일을 다시 쓰는 런처를 위해, 먼저 창을 닫도록
    /// 요청하고 응답이 없을 때만 강제 종료합니다.
    /// </summary>
    public async Task StopAsync(string processName, CancellationToken cancellationToken)
    {
        foreach (var process in Process.GetProcessesByName(NormalizeProcessName(processName)))
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        process.CloseMainWindow();
                        if (process.WaitForExit(3000))
                        {
                            continue;
                        }
                    }
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception)
                {
                    // 이미 종료된 프로세스입니다.
                }
            }
        }
    }

    public Task<bool> BringToFrontAsync(string processName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeProcessName(processName);
        var processes = Process.GetProcessesByName(normalized);
        try
        {
            var process = processes.FirstOrDefault(item => item.MainWindowHandle != IntPtr.Zero);
            if (process is null)
            {
                return Task.FromResult(false);
            }
            ShowWindow(process.MainWindowHandle, 9);
            return Task.FromResult(SetForegroundWindow(process.MainWindowHandle));
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public static string NormalizeProcessName(string processName) =>
        Path.GetFileNameWithoutExtension(processName.Trim());

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);
}
