using ManagedDrive.Cli.Core;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// Status bar text, sticky problem status and exit-save progress reporting.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Gets the status bar text.
    /// </summary>
    public string StatusText
    {
        get;
        internal set
        {
            field = value;
            _stickyStatus = null;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>
    /// The problem report <see cref="StatusText"/> currently holds, set via
    /// <see cref="ShowStickyStatus"/>, or <c>null</c> for a regular status. While set, transient
    /// messages (disk activity, the revert to <c>Status.Ready</c>, routine auto-mount progress)
    /// must not replace the report.
    /// </summary>
    private StickyStatus? _stickyStatus;

    /// <summary>
    /// Identifies what a sticky status reports, so <see cref="ClearStickyStatus"/> can remove it
    /// once that problem is resolved and it doesn't block activity status indefinitely.
    /// </summary>
    /// <param name="MountPoint">The disk the problem is about, or <c>null</c> if it can't be cleared.</param>
    /// <param name="Problem">The kind of problem on <paramref name="MountPoint"/>, or <c>null</c>.</param>
    private readonly record struct StickyStatus(string? MountPoint, string? Problem);

    /// <summary>
    /// Forces an immediate, unconditional refresh of per-disk usage and available memory,
    /// bypassing the main-window-visibility skip in <see cref="DiskViewModel.Refresh"/> and
    /// <see cref="RefreshAvailableMemory"/>. Called right before the tray tooltip is shown so it
    /// never displays data that's stale from being paused while the main window was hidden.
    /// </summary>
    internal void RefreshForTrayTooltip()
    {
        RefreshAll();
        RefreshAvailableMemory();
    }

    /// <summary>
    /// Updates <see cref="ExitSaveProgress"/>, <see cref="ExitSaveStatusText"/>, and
    /// <see cref="ExitSaveDetailText"/> during the final on-exit save. Called from
    /// <c>App.ShutdownAsync</c> via
    /// <see cref="ManagedDrive.Core.Mounting.MountManager.Dispose(Action{ManagedDrive.Core.Mounting.RamDisk, double, double, ulong})"/>.
    /// </summary>
    /// <param name="mountPoint">The mount point of the disk currently being saved.</param>
    /// <param name="overallFraction">Overall progress across all disks, in [0, 1].</param>
    /// <param name="diskFraction">Save progress for the disk currently being saved, in [0, 1].</param>
    /// <param name="totalBytes">The disk's total used bytes, for <see cref="ExitSaveDetailText"/>.</param>
    internal void ReportExitSaveProgress(string mountPoint, double overallFraction, double diskFraction, ulong totalBytes)
    {
        ExitSaveProgress = overallFraction;
        ExitSaveStatusText = Loc.Format("Msg.ExitSavingDisk", mountPoint);
        ExitSaveDetailText = Loc.Format("Busy.ByteProgress",
            ByteFormatter.Format((ulong)(totalBytes * Math.Clamp(diskFraction, 0.0, 1.0))),
            ByteFormatter.Format(totalBytes));
    }

    /// <summary>
    /// Shows a transient status-bar message naming the disk and file most recently read from
    /// or written to, reverting to <c>Status.Ready</c> after <see cref="DiskActivityStatusDuration"/>
    /// of inactivity. Driven by <see cref="DiskViewModel.ActivityObserved"/>.
    /// </summary>
    internal void ShowDiskActivityStatus(string mountPoint, bool isWrite, string filePath)
    {
        if (_stickyStatus is not null)
        {
            return;
        }

        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName))
        {
            fileName = filePath;
        }

        StatusText = Loc.Format(isWrite ? "Status.DiskWrite" : "Status.DiskRead", mountPoint, fileName);
        _diskActivityStatusTimer.Stop();
        _diskActivityStatusTimer.Start();
    }

    /// <summary>
    /// Shows a transient status-bar message, unless a problem report is shown, and reverts it to
    /// <c>Status.Ready</c> after <see cref="DiskActivityStatusDuration"/> like a disk activity message.
    /// </summary>
    /// <param name="text">The status text to show.</param>
    internal void ShowTransientStatus(string text)
    {
        if (_stickyStatus is not null)
        {
            return;
        }

        StatusText = text;
        _diskActivityStatusTimer.Stop();
        _diskActivityStatusTimer.Start();
    }

    /// <summary>
    /// Shows a problem report (a failed save or auto-mount, an adjusted capacity, a nearly full
    /// disk) in the status bar and keeps it there until an explicit status replaces it — without
    /// this, the next disk read or write would overwrite it within milliseconds, and the revert
    /// to <c>Status.Ready</c> soon after.
    /// </summary>
    /// <param name="text">The status text to show.</param>
    /// <param name="mountPoint">
    /// The disk the problem is about, if <see cref="ClearStickyStatus"/> should be able to remove
    /// the report once the problem is resolved.
    /// </param>
    /// <param name="problem">
    /// Identifies the kind of problem on <paramref name="mountPoint"/>, e.g.
    /// <c>nameof(DiskViewModel.IsHighUsage)</c>.
    /// </param>
    internal void ShowStickyStatus(string text, string? mountPoint = null, string? problem = null)
    {
        _diskActivityStatusTimer.Stop();
        StatusText = text;
        _stickyStatus = new(mountPoint, problem);
    }

    /// <summary>
    /// Reverts the status bar to <c>Status.Ready</c> if it still shows the sticky report of a
    /// problem that has since been resolved (usage dropped back below the warning threshold, or
    /// the disk is gone). Any other status is left alone.
    /// </summary>
    /// <param name="mountPoint">The disk whose problem is resolved.</param>
    /// <param name="problem">
    /// The resolved kind of problem, as passed to <see cref="ShowStickyStatus"/>, or <c>null</c>
    /// for any problem on <paramref name="mountPoint"/>.
    /// </param>
    /// <returns>Whether the status bar showed that report and was reverted.</returns>
    internal bool ClearStickyStatus(string mountPoint, string? problem = null)
    {
        if (_stickyStatus is not { MountPoint: { } source } sticky ||
            !string.Equals(source, mountPoint, StringComparison.OrdinalIgnoreCase) ||
            (problem is not null && sticky.Problem != problem))
        {
            return false;
        }

        StatusText = Loc.Get("Status.Ready");
        return true;
    }

    /// <summary>
    /// Shows a routine status message unless a problem report from <see cref="ShowStickyStatus"/>
    /// is currently shown, so e.g. a later disk's successful auto-mount doesn't hide an earlier
    /// one's failure.
    /// </summary>
    /// <param name="text">The status text to show.</param>
    internal void ShowStatusUnlessSticky(string text)
    {
        if (_stickyStatus is null)
        {
            StatusText = text;
        }
    }
}
