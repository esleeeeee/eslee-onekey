using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Eslee.OneKey.App;

/// <summary>붙은 창이 기준 창의 어느 쪽에 있는지입니다.</summary>
public enum DockSide
{
    None,
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// 창을 끌다가 기준 창 가까이 가면 그 변에 착 붙게 합니다. 상하좌우 어느 쪽이든 붙고,
/// 붙은 뒤에는 모서리도 맞춥니다. 끌기가 끝나면 어디에 붙었는지 알려 줍니다.
/// </summary>
public sealed class WindowMagnet
{
    /// <summary>붙었을 때 두 창 사이에 두는 틈입니다.</summary>
    public const double GapDip = 6;

    private const double ThresholdDip = 18;
    private const int WmMoving = 0x0216;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;

    private readonly Window _moving;
    private readonly Func<Window?> _target;
    private Point _grab;
    private DockSide _side;
    private double _offsetPixels;

    public WindowMagnet(Window moving, Func<Window?> target)
    {
        _moving = moving;
        _target = target;
        if (new WindowInteropHelper(moving).Handle != IntPtr.Zero)
        {
            Attach();
        }
        else
        {
            moving.SourceInitialized += (_, _) => Attach();
        }
    }

    /// <summary>끌기가 끝났습니다. 붙은 쪽과, 그 변을 따라 밀린 거리(DIP)를 알려 줍니다.</summary>
    public event Action<DockSide, double>? Dropped;

    private void Attach() =>
        HwndSource.FromHwnd(new WindowInteropHelper(_moving).Handle)?.AddHook(WndProc);

    private IntPtr WndProc(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmEnterSizeMove:
                // 붙인 뒤에도 마우스를 계속 따라가려면 처음 잡은 위치를 기억해야 한다.
                // Windows가 주는 좌표는 우리가 고친 값에서 이어지므로 그대로 쓰면 떨어지지 않는다.
                if (GetCursorPos(out var cursor) && GetWindowRect(handle, out var origin))
                {
                    _grab = new Point { X = cursor.X - origin.Left, Y = cursor.Y - origin.Top };
                }
                _side = DockSide.None;
                break;

            case WmMoving:
                if (TargetRect() is { } target && GetCursorPos(out var position))
                {
                    var proposed = Marshal.PtrToStructure<Rect>(lParam);
                    var width = proposed.Right - proposed.Left;
                    var height = proposed.Bottom - proposed.Top;
                    var free = new Rect
                    {
                        Left = position.X - _grab.X,
                        Top = position.Y - _grab.Y,
                    };
                    free.Right = free.Left + width;
                    free.Bottom = free.Top + height;

                    var scale = VisualTreeHelper.GetDpi(_moving).DpiScaleX;
                    (_side, _offsetPixels) = Snap(
                        ref free,
                        target,
                        (int)Math.Round(ThresholdDip * scale),
                        (int)Math.Round(GapDip * scale));
                    Marshal.StructureToPtr(free, lParam, fDeleteOld: false);
                    handled = true;
                    return 1;
                }
                break;

            case WmExitSizeMove:
                Dropped?.Invoke(_side, _offsetPixels / VisualTreeHelper.GetDpi(_moving).DpiScaleX);
                break;
        }
        return IntPtr.Zero;
    }

    private Rect? TargetRect()
    {
        if (_target() is not { IsVisible: true } window)
        {
            return null;
        }
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && GetWindowRect(handle, out var rect) ? rect : null;
    }

    /// <summary>
    /// 가까운 변에 붙이고, 붙은 변을 따라 모서리가 가까우면 그것도 맞춥니다. 좌표는 모두
    /// 화면 픽셀입니다. 붙은 쪽과 그 변을 따라 밀린 거리를 돌려줍니다.
    /// </summary>
    internal static (DockSide Side, double Offset) Snap(ref Rect moving, Rect target, int threshold, int gap)
    {
        var width = moving.Right - moving.Left;
        var height = moving.Bottom - moving.Top;
        var left = moving.Left;
        var top = moving.Top;
        var side = DockSide.None;

        var besideVertically = top < target.Bottom + threshold && top + height > target.Top - threshold;
        var besideHorizontally = left < target.Right + threshold && left + width > target.Left - threshold;

        if (besideVertically && Math.Abs(left - (target.Right + gap)) <= threshold)
        {
            left = target.Right + gap;
            side = DockSide.Right;
        }
        else if (besideVertically && Math.Abs(left + width - (target.Left - gap)) <= threshold)
        {
            left = target.Left - gap - width;
            side = DockSide.Left;
        }
        else if (besideHorizontally && Math.Abs(top - (target.Bottom + gap)) <= threshold)
        {
            top = target.Bottom + gap;
            side = DockSide.Bottom;
        }
        else if (besideHorizontally && Math.Abs(top + height - (target.Top - gap)) <= threshold)
        {
            top = target.Top - gap - height;
            side = DockSide.Top;
        }

        if (side is DockSide.Left or DockSide.Right)
        {
            if (Math.Abs(top - target.Top) <= threshold)
            {
                top = target.Top;
            }
            else if (Math.Abs(top + height - target.Bottom) <= threshold)
            {
                top = target.Bottom - height;
            }
        }
        else if (side is DockSide.Top or DockSide.Bottom)
        {
            if (Math.Abs(left - target.Left) <= threshold)
            {
                left = target.Left;
            }
            else if (Math.Abs(left + width - target.Right) <= threshold)
            {
                left = target.Right - width;
            }
        }

        moving = new Rect { Left = left, Top = top, Right = left + width, Bottom = top + height };
        return side switch
        {
            DockSide.Left or DockSide.Right => (side, top - target.Top),
            DockSide.Top or DockSide.Bottom => (side, left - target.Left),
            _ => (DockSide.None, 0),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
}
