namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Places windows in the middle of the primary monitor. <see cref="WindowStartupLocation.CenterScreen"/>
/// would follow the mouse to whichever monitor it is on, which is arbitrary at startup (for example
/// when the app starts at logon) and can differ between windows opened a moment apart.
/// </summary>
internal static class PrimaryMonitorPlacement
{
    /// <summary>
    /// Computes the top-left position that centers a window of the given size in a work area. A
    /// window larger than the work area (a small or heavily scaled monitor) is aligned to its top-left
    /// corner instead, so the title bar with the close button never ends up off-screen.
    /// </summary>
    /// <param name="workArea">The work area to center in, in device-independent units.</param>
    /// <param name="width">The window width.</param>
    /// <param name="height">The window height.</param>
    /// <returns>The left and top coordinates.</returns>
    public static (double Left, double Top) CenterInWorkArea(Rect workArea, double width, double height) =>
        (workArea.Left + Math.Max(0, (workArea.Width - width) / 2), workArea.Top + Math.Max(0, (workArea.Height - height) / 2));

    /// <summary>
    /// Centers a window in the work area of the primary monitor. Call it before the window is shown;
    /// it switches the window's startup location to <see cref="WindowStartupLocation.Manual"/>.
    /// </summary>
    /// <param name="window">The window, whose <see cref="FrameworkElement.Width"/> and
    /// <see cref="FrameworkElement.Height"/> are set.</param>
    public static void CenterOnPrimaryMonitor(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        (window.Left, window.Top) = CenterInWorkArea(SystemParameters.WorkArea, window.Width, window.Height);
    }
}
