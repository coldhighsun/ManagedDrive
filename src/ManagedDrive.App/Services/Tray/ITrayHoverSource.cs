namespace ManagedDrive.App.Services;

/// <summary>
/// The tray icon events that drive <see cref="TrayTooltipController"/>, split out so the
/// controller can be tested without a real tray icon.
/// </summary>
internal interface ITrayHoverSource
{
    /// <summary>
    /// Raised whenever the cursor moves over the tray icon, carrying its current screen position.
    /// </summary>
    event Action<System.Drawing.Point>? MouseMoved;

    /// <summary>
    /// Raised right before the tray context menu is shown.
    /// </summary>
    event Action? ContextMenuOpening;

    /// <summary>
    /// Gets a value indicating whether the tray context menu is currently showing.
    /// </summary>
    bool IsContextMenuVisible { get; }
}
