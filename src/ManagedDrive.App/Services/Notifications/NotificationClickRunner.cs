namespace ManagedDrive.App.Services;

/// <summary>
/// Takes the user where a clicked balloon tip points. The main window always comes up first. A disk
/// that has gone since the balloon was shown leaves just the window, and a dialog is only opened
/// when nothing else is in the way, so a click never stacks a dialog on an open one.
/// </summary>
/// <param name="showMainWindow">Brings the main window to the user.</param>
/// <param name="selectDisk">Selects the disk at a mount point; returns whether it still exists.</param>
/// <param name="canOpenDialog">Returns whether a dialog can be opened right now.</param>
/// <param name="editSelectedDisk">Opens the selected disk's edit dialog.</param>
/// <param name="showUpdateDialog">Opens the update dialog.</param>
internal sealed class NotificationClickRunner(
    Action showMainWindow,
    Func<string, bool> selectDisk,
    Func<bool> canOpenDialog,
    Action editSelectedDisk,
    Action showUpdateDialog)
{
    /// <summary>
    /// Carries out a click.
    /// </summary>
    /// <param name="target">Where the clicked balloon tip points.</param>
    public void Run(NotificationClickTarget target)
    {
        showMainWindow();

        switch (target.Kind)
        {
            case NotificationClickKind.Disk:
                SelectDisk(target);
                break;

            case NotificationClickKind.EditDisk:
                if (SelectDisk(target) && canOpenDialog())
                {
                    editSelectedDisk();
                }

                break;

            case NotificationClickKind.Update:
                if (canOpenDialog())
                {
                    showUpdateDialog();
                }

                break;
        }
    }

    /// <summary>
    /// Selects the disk a target names.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <returns>Whether the disk still exists.</returns>
    private bool SelectDisk(NotificationClickTarget target) =>
        target.MountPoint is { } mountPoint && selectDisk(mountPoint);
}
