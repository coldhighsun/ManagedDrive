namespace ManagedDrive.App.Services;

/// <summary>
/// Pure bookkeeping for dragging the borderless splash by hand: from the cursor's screen position
/// at the start of the drag and its current one, works out where the window goes. Everything is in
/// device pixels, so a change of the window's DPI mid-drag needs no conversion, only a fresh
/// <see cref="Begin"/>. Used instead of <c>Window.DragMove</c>, whose modal move loop made the
/// splash hitch when the mouse button was released.
/// </summary>
public sealed class SplashDragTracker
{
    /// <summary>
    /// The cursor's screen position when the drag began.
    /// </summary>
    private (int X, int Y) _cursorStart;

    /// <summary>
    /// The window's position when the drag began.
    /// </summary>
    private (int X, int Y) _windowStart;

    /// <summary>
    /// Whether a drag is in progress.
    /// </summary>
    public bool IsDragging { get; private set; }

    /// <summary>
    /// Starts a drag, replacing any earlier one.
    /// </summary>
    /// <param name="cursorX">The cursor's screen X in device pixels.</param>
    /// <param name="cursorY">The cursor's screen Y in device pixels.</param>
    /// <param name="windowLeft">The window's left edge in device pixels.</param>
    /// <param name="windowTop">The window's top edge in device pixels.</param>
    public void Begin(int cursorX, int cursorY, int windowLeft, int windowTop)
    {
        _cursorStart = (cursorX, cursorY);
        _windowStart = (windowLeft, windowTop);
        IsDragging = true;
    }

    /// <summary>
    /// Works out the window position for the cursor's current position.
    /// </summary>
    /// <param name="cursorX">The cursor's screen X in device pixels.</param>
    /// <param name="cursorY">The cursor's screen Y in device pixels.</param>
    /// <returns>The new left and top in device pixels, or <c>null</c> when no drag is in progress.</returns>
    public (int Left, int Top)? Move(int cursorX, int cursorY)
    {
        if (!IsDragging)
        {
            return null;
        }

        return (
            _windowStart.X + cursorX - _cursorStart.X,
            _windowStart.Y + cursorY - _cursorStart.Y);
    }

    /// <summary>
    /// Ends the drag; later moves do nothing until the next <see cref="Begin"/>.
    /// </summary>
    public void End() => IsDragging = false;
}
