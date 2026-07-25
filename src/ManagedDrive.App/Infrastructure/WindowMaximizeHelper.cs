using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Fixes WM_GETMINMAXINFO for borderless (WindowStyle="None" + AllowsTransparency="True") windows
/// so maximizing snaps to the current monitor's work area instead of covering the taskbar.
/// </summary>
internal static class WindowMaximizeHelper
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public static void HookMaximizeBehavior(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (HwndSource.FromHwnd(handle) is HwndSource source)
            {
                source.AddHook(WndProc);
            }
        };

        // Belt-and-suspenders for borderless + transparent (layered) windows, where WPF's
        // own maximize sizing can ignore WM_GETMINMAXINFO and grow the window to the full
        // monitor (sliding the bottom under the taskbar): re-clamp to the work area whenever
        // the window enters the maximized state.
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Maximized)
            {
                ClampMaximizedToWorkArea(window);
            }
        };
    }

    private static void ClampMaximizedToWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var workArea = monitorInfo.rcWork;
        // MoveWindow expects physical pixels; WM_GETMINMAXINFO/GetMonitorInfo are already in
        // physical pixels, so no DIP conversion is needed for a PerMonitorV2-aware WPF app.
        MoveWindow(
            handle,
            workArea.Left,
            workArea.Top,
            workArea.Right - workArea.Left,
            workArea.Bottom - workArea.Top,
            true);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            ApplyWorkAreaBounds(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ApplyWorkAreaBounds(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var workArea = monitorInfo.rcWork;
        var monitorArea = monitorInfo.rcMonitor;

        var minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        minMaxInfo.ptMaxPosition.X = workArea.Left - monitorArea.Left;
        minMaxInfo.ptMaxPosition.Y = workArea.Top - monitorArea.Top;
        minMaxInfo.ptMaxSize.X = workArea.Right - workArea.Left;
        minMaxInfo.ptMaxSize.Y = workArea.Bottom - workArea.Top;
        // Hard-clamp the maximum tracked size to the work area so a borderless + transparent
        // (layered) window can't be grown past it and slide under the taskbar when maximized.
        minMaxInfo.ptMaxTrackSize.X = workArea.Right - workArea.Left;
        minMaxInfo.ptMaxTrackSize.Y = workArea.Bottom - workArea.Top;
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
