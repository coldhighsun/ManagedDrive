using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ManagedDrive.App;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// </summary>
public partial class MainWindow
{
    private const uint MonitorDefaultToNearest = 2;
    private const int WindowsMessageGetMinMaxInfo = 0x0024;

    /// <summary>
    /// Initializes the main window and binds the supplied view model.
    /// </summary>
    /// <param name="viewModel">The view model to bind as <c>DataContext</c>.</param>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// Hooks the window's message loop so maximizing clamps to the monitor's
    /// work area instead of its full bounds, which WPF does not do automatically
    /// for <c>WindowStyle="None"</c> windows and would otherwise cover the taskbar.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var workArea = monitorInfo.rcWork;
        var monitorArea = monitorInfo.rcMonitor;

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMaxInfo.ptMaxPosition.X = workArea.Left - monitorArea.Left;
        minMaxInfo.ptMaxPosition.Y = workArea.Top - monitorArea.Top;
        minMaxInfo.ptMaxSize.X = workArea.Right - workArea.Left;
        minMaxInfo.ptMaxSize.Y = workArea.Bottom - workArea.Top;
        Marshal.StructureToPtr(minMaxInfo, lParam, fDeleteOld: true);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WindowsMessageGetMinMaxInfo)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OverflowBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: not null } btn)
        {
            btn.ContextMenu.DataContext = DataContext;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}