using System.Runtime.InteropServices;

namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// The user32 declarations shared by the window code, so that each view or helper does not declare
/// its own copy of the same structs and imports.
/// </summary>
internal static class WindowNativeMethods
{
    /// <summary>
    /// <c>SWP_NOSIZE</c>: keep the current size.
    /// </summary>
    public const uint SwpNoSize = 0x0001;

    /// <summary>
    /// <c>SWP_NOZORDER</c>: keep the current z-order.
    /// </summary>
    public const uint SwpNoZOrder = 0x0004;

    /// <summary>
    /// <c>SWP_NOACTIVATE</c>: do not activate the window.
    /// </summary>
    public const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// Only move the window: no resize, no z-order change, no activation.
    /// </summary>
    public const uint SwpMoveOnly = SwpNoSize | SwpNoZOrder | SwpNoActivate;

    /// <summary>
    /// A point in screen coordinates (<c>POINT</c>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        /// <summary>
        /// Horizontal position.
        /// </summary>
        public int X;

        /// <summary>
        /// Vertical position.
        /// </summary>
        public int Y;
    }

    /// <summary>
    /// A rectangle in screen coordinates (<c>RECT</c>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        /// <summary>
        /// Left edge.
        /// </summary>
        public int Left;

        /// <summary>
        /// Top edge.
        /// </summary>
        public int Top;

        /// <summary>
        /// Right edge.
        /// </summary>
        public int Right;

        /// <summary>
        /// Bottom edge.
        /// </summary>
        public int Bottom;
    }

    /// <summary>
    /// Reads the cursor's position in screen pixels.
    /// </summary>
    /// <param name="point">Receives the position.</param>
    /// <returns>Whether the call succeeded.</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    /// <summary>
    /// Reads a window's bounds in screen pixels.
    /// </summary>
    /// <param name="hWnd">The window handle.</param>
    /// <param name="rect">Receives the bounds.</param>
    /// <returns>Whether the call succeeded.</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out Rect rect);

    /// <summary>
    /// Moves, resizes or re-orders a window in one call.
    /// </summary>
    /// <param name="hWnd">The window handle.</param>
    /// <param name="hWndInsertAfter">The window to place it after; ignored with <see cref="SwpNoZOrder"/>.</param>
    /// <param name="x">The new left edge in device pixels.</param>
    /// <param name="y">The new top edge in device pixels.</param>
    /// <param name="cx">The new width; ignored with <see cref="SwpNoSize"/>.</param>
    /// <param name="cy">The new height; ignored with <see cref="SwpNoSize"/>.</param>
    /// <param name="flags">The <c>SWP_*</c> flags.</param>
    /// <returns>Whether the call succeeded.</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
