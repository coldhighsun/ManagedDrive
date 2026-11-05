namespace ManagedDrive.App.Services;

/// <summary>
/// What clicking a balloon tip leads to.
/// </summary>
public enum NotificationClickKind
{
    /// <summary>
    /// Shows the main window.
    /// </summary>
    ShowWindow,

    /// <summary>
    /// Shows the main window with a disk selected.
    /// </summary>
    Disk,

    /// <summary>
    /// Shows the main window with a disk selected and opens that disk's edit dialog.
    /// </summary>
    EditDisk,

    /// <summary>
    /// Shows the main window and opens the update dialog.
    /// </summary>
    Update,
}

/// <summary>
/// The balloon tips about a single disk, other than a refused write
/// (see <see cref="NotificationClickTarget.ForWriteRejection"/>).
/// </summary>
public enum DiskNotificationKind
{
    /// <summary>
    /// The disk went over its high-usage threshold.
    /// </summary>
    HighUsage,

    /// <summary>
    /// The disk's capacity was raised on load to hold its content.
    /// </summary>
    CapacityAdjusted,

    /// <summary>
    /// A save the user started finished.
    /// </summary>
    SaveCompleted,

    /// <summary>
    /// Saving the disk failed.
    /// </summary>
    SaveFailed,

    /// <summary>
    /// A save succeeded again after a failure.
    /// </summary>
    SaveRecovered,
}

/// <summary>
/// Where a click on a balloon tip takes the user. A disk is named by its mount point rather than
/// its view model because the click can come long after the balloon was shown, when the disk may
/// have been unmounted or remounted, so it is looked up again at that moment.
/// </summary>
/// <param name="Kind">What the click leads to.</param>
/// <param name="MountPoint">The disk concerned; <c>null</c> for targets that name no disk.</param>
public sealed record NotificationClickTarget(NotificationClickKind Kind, string? MountPoint = null)
{
    /// <summary>
    /// Gets the target that only shows the main window.
    /// </summary>
    public static NotificationClickTarget ShowWindow { get; } = new(NotificationClickKind.ShowWindow);

    /// <summary>
    /// Gets the target that opens the update dialog.
    /// </summary>
    public static NotificationClickTarget Update { get; } = new(NotificationClickKind.Update);

    /// <summary>
    /// Creates a target that selects a disk in the main window.
    /// </summary>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <returns>The target.</returns>
    public static NotificationClickTarget Disk(string mountPoint) => new(NotificationClickKind.Disk, mountPoint);

    /// <summary>
    /// Creates a target that opens a disk's edit dialog.
    /// </summary>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <returns>The target.</returns>
    public static NotificationClickTarget EditDisk(string mountPoint) => new(NotificationClickKind.EditDisk, mountPoint);

    /// <summary>
    /// Gets the target of a balloon tip about a disk. Reports the user answers by changing the
    /// disk's size open its edit dialog; the others just select the disk, whose status is in the
    /// status bar.
    /// </summary>
    /// <param name="kind">The balloon tip.</param>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <returns>The target.</returns>
    public static NotificationClickTarget ForDisk(DiskNotificationKind kind, string mountPoint) => kind switch
    {
        DiskNotificationKind.HighUsage or DiskNotificationKind.CapacityAdjusted => EditDisk(mountPoint),
        _ => Disk(mountPoint),
    };

    /// <summary>
    /// Gets the target of a refused-write balloon tip. A larger disk helps when it is full, but
    /// not when the system is out of memory.
    /// </summary>
    /// <param name="reason">Why the write was refused.</param>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <returns>The target.</returns>
    public static NotificationClickTarget ForWriteRejection(WriteRejectionReason reason, string mountPoint) =>
        reason == WriteRejectionReason.LowMemory ? Disk(mountPoint) : EditDisk(mountPoint);
}
