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
