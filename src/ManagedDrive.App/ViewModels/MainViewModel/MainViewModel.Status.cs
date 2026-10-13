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
    /// The aggregate state of the exit save, written by the disks' save threads and read on the UI
    /// thread. It is created on the UI thread by <see cref="BeginExitSave"/> before the disks start.
    /// </summary>
    private ExitSaveProgressTracker? _exitSaveTracker;

    /// <summary>
    /// Starts a fresh exit save covering the disks that will be written, so the total and the
    /// weights are fixed before the first report arrives.
    /// </summary>
    /// <param name="totalBytesByMountPoint">Used bytes of every disk about to be written, keyed by mount point.</param>
    internal void BeginExitSave(IReadOnlyDictionary<string, ulong> totalBytesByMountPoint) =>
        _exitSaveTracker = new(totalBytesByMountPoint);

    /// <summary>
    /// Records one disk's save progress during the final on-exit save. Called from the disks' save
    /// threads via
    /// <see cref="ManagedDrive.Core.Mounting.MountManager.Dispose(Action{ManagedDrive.Core.Mounting.RamDisk, double})"/>,
    /// so it only updates the tracker; <see cref="ShowExitSaveProgress"/> puts the result on screen.
    /// </summary>
    /// <param name="mountPoint">The mount point of the disk currently being saved.</param>
    /// <param name="diskFraction">Save progress for that disk, in [0, 1].</param>
    /// <returns>Whether the report changed what the overlay shows, i.e. a refresh is worthwhile.</returns>
    internal bool ReportExitSaveProgress(string mountPoint, double diskFraction) =>
        _exitSaveTracker?.Report(mountPoint, diskFraction) ?? false;

    /// <summary>
    /// Records that a disk's final save failed, so it is not shown as finished. Thread-safe. It only
    /// counts once <see cref="BeginExitSave"/> has run and that disk's own final save has begun (it
    /// has reported), so a periodic auto-save failure does not mark a disk whose final save then
    /// succeeds.
    /// </summary>
    /// <param name="mountPoint">The mount point of the disk whose save failed.</param>
    internal void MarkExitSaveFailed(string mountPoint) => _exitSaveTracker?.MarkFailed(mountPoint);

    /// <summary>
    /// Updates <see cref="ExitSaveProgress"/>, <see cref="ExitSaveStatusText"/> and
    /// <see cref="ExitSaveDetailText"/> from the newest state. Disks are saved in parallel, so the
    /// overlay shows the aggregate instead of whichever disk reported last (which made the text
    /// jump). UI thread only.
    /// </summary>
    internal void ShowExitSaveProgress()
    {
        if (_exitSaveTracker is { DiskCount: > 0 } tracker)
        {
            ApplyExitSaveSnapshot(tracker.Sample());
        }
    }

    /// <summary>
    /// Shows the exit save as finished, once the disks have all been disposed. UI thread only.
    /// </summary>
    /// <param name="markAllFinished">
    /// <c>false</c> when disposing the disks threw, so disks that did not report are not claimed as
    /// done.
    /// </param>
    internal void CompleteExitSave(bool markAllFinished = true)
    {
        if (_exitSaveTracker is null)
        {
            return;
        }

        if (_exitSaveTracker.DiskCount == 0)
        {
            // Nothing was written: keep the generic text, just fill the bar.
            if (markAllFinished)
            {
                ExitSaveProgress = 1;
            }

            return;
        }

        if (markAllFinished)
        {
            _exitSaveTracker.Complete();
        }

        ApplyExitSaveSnapshot(_exitSaveTracker.Sample());
    }

    /// <summary>
    /// Puts a tracker snapshot into the three exit-save properties.
    /// </summary>
    /// <param name="s">The snapshot to show.</param>
    private void ApplyExitSaveSnapshot(ExitSaveProgressTracker.Snapshot s)
    {
        ExitSaveProgress = s.Overall;
        ExitSaveStatusText = s.EndedWithFailures ? Loc.Format("Msg.ExitSavingFailed", s.FailedCount, s.DiskCount)
            : s.IsFinishing ? Loc.Get("Msg.ExitSavingFinishing")
            : s.MountPoint is { } single ? Loc.Format("Msg.ExitSavingDisk", single)
            : Loc.Format("Msg.ExitSavingDisks", s.DiskCount, s.CompletedCount);
        ExitSaveDetailText = Loc.Format("Busy.ByteProgress",
            ByteFormatter.Format(s.DoneBytes),
            ByteFormatter.Format(s.TotalBytes));
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
