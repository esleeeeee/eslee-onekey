using System.Diagnostics;
using Eslee.OneKey.Core;

namespace Eslee.OneKey.Infrastructure.Windows;

public sealed class PollingProcessMonitor : IProcessMonitor
{
    private CancellationTokenSource? _lifetime;
    private Task? _monitorTask;

    public event Func<Task>? ProcessStarted;
    public event Func<Task>? ProcessExited;

    public Task StartAsync(
        string processName,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        if (_monitorTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }
        if (interval < TimeSpan.FromMilliseconds(500))
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "감지 간격은 500ms 이상이어야 합니다.");
        }

        var normalized = WindowsProcessService.NormalizeProcessName(processName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("감지할 프로세스 이름이 필요합니다.", nameof(processName));
        }

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorTask = MonitorAsync(normalized, interval, _lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync();
        }
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping the monitor.
            }
        }
        _monitorTask = null;
        _lifetime?.Dispose();
        _lifetime = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task MonitorAsync(
        string processName,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var wasRunning = IsRunning(processName);
        if (wasRunning)
        {
            await InvokeHandlersAsync(ProcessStarted);
        }

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var isRunning = IsRunning(processName);
            if (isRunning && !wasRunning)
            {
                await InvokeHandlersAsync(ProcessStarted);
            }
            else if (!isRunning && wasRunning)
            {
                await InvokeHandlersAsync(ProcessExited);
            }
            wasRunning = isRunning;
        }
    }

    private static bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static async Task InvokeHandlersAsync(Func<Task>? handlers)
    {
        if (handlers is null)
        {
            return;
        }
        foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
        {
            await handler();
        }
    }
}
