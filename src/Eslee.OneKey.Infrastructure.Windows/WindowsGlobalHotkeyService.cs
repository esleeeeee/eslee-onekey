using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Eslee.OneKey.Core;

namespace Eslee.OneKey.Infrastructure.Windows;

public sealed class WindowsGlobalHotkeyService : IHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4F4B;
    private readonly IntPtr _windowHandle;
    private readonly HwndSource _source;
    private bool _registered;

    public WindowsGlobalHotkeyService(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("WPF window source를 찾을 수 없습니다.");
        _source.AddHook(WindowProcedure);
    }

    public event Func<Task>? Pressed;

    public Task<HotkeyRegistrationResult> RegisterAsync(
        HotkeyGesture gesture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Unregister();
        if (!TryGetVirtualKey(gesture.Key, out var virtualKey))
        {
            return Task.FromResult(new HotkeyRegistrationResult(false, "지원하지 않는 단축키입니다."));
        }

        var modifiers = (gesture.Alt ? 0x0001u : 0u)
            | (gesture.Control ? 0x0002u : 0u)
            | (gesture.Shift ? 0x0004u : 0u)
            | (gesture.Windows ? 0x0008u : 0u)
            | 0x4000u;
        _registered = RegisterHotKey(_windowHandle, HotkeyId, modifiers, virtualKey);
        if (_registered)
        {
            return Task.FromResult(new HotkeyRegistrationResult(true));
        }

        var error = new Win32Exception(Marshal.GetLastWin32Error());
        return Task.FromResult(new HotkeyRegistrationResult(
            false,
            $"전역 단축키를 등록할 수 없습니다. 다른 프로그램과 충돌할 수 있습니다. ({error.NativeErrorCode})"));
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
            _registered = false;
        }
    }

    public ValueTask DisposeAsync()
    {
        Unregister();
        _source.RemoveHook(WindowProcedure);
        return ValueTask.CompletedTask;
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message == WmHotkey && wordParameter.ToInt32() == HotkeyId)
        {
            handled = true;
            _ = InvokePressedAsync();
        }
        return IntPtr.Zero;
    }

    private async Task InvokePressedAsync()
    {
        if (Pressed is null)
        {
            return;
        }
        foreach (var handler in Pressed.GetInvocationList().Cast<Func<Task>>())
        {
            await handler();
        }
    }

    private static bool TryGetVirtualKey(string key, out uint virtualKey)
    {
        virtualKey = 0;
        var normalized = key.Trim().ToUpperInvariant();
        if (normalized.Length == 1 && char.IsLetterOrDigit(normalized[0]))
        {
            virtualKey = normalized[0];
            return true;
        }
        if (normalized.StartsWith('F') &&
            int.TryParse(normalized.AsSpan(1), out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            return true;
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr windowHandle,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
