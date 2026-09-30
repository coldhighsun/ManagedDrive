namespace ManagedDrive.App.Views;

/// <summary>
/// Placement helpers for dialogs that can be shown while the main window is hidden.
/// </summary>
internal static class DialogPlacement
{
    /// <summary>
    /// Makes an ownerless dialog reachable. The dialogs are created without a taskbar button and
    /// normally follow their owner; with the main window hidden (started minimized, or closed to
    /// the tray) there is no owner, so without this the dialog can open behind other windows with
    /// no way to find it, and whatever awaits its result waits forever.
    /// </summary>
    /// <param name="dialog">The dialog that was just loaded.</param>
    public static void BringToFrontWhenUnowned(Window dialog)
    {
        if (dialog.Owner is not null)
        {
            return;
        }

        dialog.ShowInTaskbar = true;
        dialog.Topmost = true;
        dialog.Activate();
    }
}
