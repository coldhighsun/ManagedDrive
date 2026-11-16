namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Pure rules for the drag-and-drop hint of the main window.
/// </summary>
public static class DragPositionRules
{
    /// <summary>
    /// Whether the pointer of a drag has left the window. <c>DragLeave</c> also bubbles up from child elements when the
    /// pointer only moves from one of them to another, so the hint is hidden only when the pointer is really outside.
    /// </summary>
    /// <param name="x">The pointer's X in the window's coordinates.</param>
    /// <param name="y">The pointer's Y in the window's coordinates.</param>
    /// <param name="width">The window's width.</param>
    /// <param name="height">The window's height.</param>
    /// <returns><c>true</c> when the pointer is outside the window.</returns>
    public static bool IsOutside(double x, double y, double width, double height) =>
        x < 0 || y < 0 || x >= width || y >= height;
}
