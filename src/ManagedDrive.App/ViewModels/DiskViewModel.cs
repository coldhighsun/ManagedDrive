using ManagedDrive.Cli.Core;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// View model that wraps a live <see cref="RamDisk"/> and exposes formatted, bindable
/// properties for display in the disk list. Usage statistics are refreshed automatically
/// on a background timer.
/// </summary>
public sealed class DiskViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// Gap between the high-usage warning threshold and the reset threshold that re-arms the
    /// warning, providing hysteresis without exposing a second setting to the user.
    /// </summary>
    private const double HighUsageResetGap = 5.0;

    /// <summary>
    /// Throttle window for <see cref="ActivityObserved"/>: the first access in a burst is
    /// reported immediately, subsequent accesses within this window are coalesced into a single
    /// trailing report when it elapses.
    /// </summary>
    private static readonly TimeSpan ActivityThrottleWindow = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _activityThrottleTimer;
    private readonly DispatcherTimer _refreshTimer;

    private bool _activityTrackingEnabled;
    private ulong _freeBytes;
    private bool _isCurrentTempDir;
    private DiskActivityEventArgs? _pendingActivity;
    private ulong _usedBytes;

    /// <summary>
    /// Initializes a new view model for <paramref name="disk"/>.
    /// </summary>
    /// <param name="disk">The live RAM disk to represent.</param>
    public DiskViewModel(RamDisk disk)
    {
        Disk = disk;
        _usedBytes = disk.UsedBytes;
        _freeBytes = disk.FreeBytes;
        _isCurrentTempDir = CheckIsCurrentTempDir();

        OpenInExplorerCommand = new(_ => Process.Start("explorer.exe", MountPoint));
        OpenImageDirectoryCommand = new(
            _ => Process.Start("explorer.exe", Path.GetDirectoryName(SourcePath)!),
            _ => HasImagePath);

        _refreshTimer = new()
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();

        _activityThrottleTimer = new()
        {
            Interval = ActivityThrottleWindow
        };
        _activityThrottleTimer.Tick += OnActivityThrottleTick;

        Disk.SaveFailed += OnDiskSaveFailed;
    }

    /// <summary>
    /// Occurs when this disk has been read from or written to, naming the file most recently
    /// touched. Pushed directly from <see cref="RamDisk.ContentAccessed"/> rather than polled,
    /// throttled to at most once per <see cref="ActivityThrottleWindow"/>: the first access in a
    /// burst is reported immediately, and any further accesses within the window are coalesced
    /// into one trailing report when it elapses. Always raised on the UI dispatcher thread.
    /// </summary>
    public event EventHandler<DiskActivityEventArgs>? ActivityObserved;

    /// <summary>
    /// Occurs when disk usage reaches or exceeds the configured high-usage warning threshold.
    /// </summary>
    public event EventHandler? HighUsageWarning;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Occurs when an image save or snapshot write fails for this disk. Always raised on the
    /// UI dispatcher thread, since the underlying <see cref="RamDisk.SaveFailed"/> event may
    /// originate from a background auto-save timer thread.
    /// </summary>
    public event EventHandler<Exception>? SaveFailed;

    /// <summary>
    /// Gets whether this disk's effective capacity was automatically raised at mount time
    /// because the loaded image's actual content exceeded the configured capacity. This is a
    /// one-time fact determined during mount, not an ongoing event.
    /// </summary>
    public bool CapacityAdjustedOnLoad => Disk.OriginalCapacityBytesOnLoad.HasValue;

    /// <summary>
    /// Gets the total capacity formatted as a human-readable string.
    /// </summary>
    public string CapacityFormatted => ByteFormatter.Format(Disk.TotalBytes);

    /// <summary>
    /// Gets the underlying <see cref="RamDisk"/> instance.
    /// </summary>
    public RamDisk Disk
    {
        get;
    }

    /// <summary>
    /// Gets the amount of free space formatted as a human-readable string.
    /// </summary>
    public string FreeFormatted => ByteFormatter.Format(_freeBytes);

    /// <summary>
    /// Gets the free-space percentage (0–100) for display.
    /// </summary>
    public double FreePercent =>
        Disk.TotalBytes > 0
            ? Math.Round((double)_freeBytes / Disk.TotalBytes * 100.0, 1)
            : 100.0;

    /// <summary>
    /// Gets whether this disk has a backing image file or source archive configured.
    /// </summary>
    public bool HasImagePath => !string.IsNullOrEmpty(SourcePath);

    /// <summary>
    /// Gets whether the user's TEMP and TMP currently point to this disk's Temp folder.
    /// </summary>
    public bool IsCurrentTempDir
    {
        get => _isCurrentTempDir;
        private set
        {
            if (_isCurrentTempDir == value)
            {
                return;
            }

            _isCurrentTempDir = value;
            OnPropertyChanged(nameof(IsCurrentTempDir));
            OnPropertyChanged(nameof(IsNotCurrentTempDir));
        }
    }

    /// <summary>
    /// Gets whether disk usage is at or above the high-usage warning threshold.
    /// </summary>
    public bool IsHighUsage
    {
        get; private set;
    }

    /// <summary>
    /// Gets the inverse of <see cref="IsCurrentTempDir"/> for visibility binding.
    /// </summary>
    public bool IsNotCurrentTempDir => !_isCurrentTempDir;

    /// <summary>
    /// Gets the inverse of <see cref="IsReadOnly"/> for visibility binding.
    /// </summary>
    public bool IsNotReadOnly => !Disk.Options.ReadOnly;

    /// <summary>
    /// Gets whether this disk's backing image is password-protected.
    /// </summary>
    public bool IsPasswordProtected => Disk.IsPasswordProtected;

    /// <summary>
    /// Gets whether this disk is read-only.
    /// </summary>
    public bool IsReadOnly => Disk.Options.ReadOnly;

    /// <summary>
    /// Gets or sets whether this disk is being unmounted and remounted with new options,
    /// driving the "applying changes" overlay on the disk card.
    /// </summary>
    public bool IsRemounting
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(IsRemounting));
        }
    }

    /// <summary>
    /// Gets or sets whether this disk's image is currently being saved, driving the
    /// "saving" overlay on the disk card.
    /// </summary>
    public bool IsSaving
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(IsSaving));
        }
    }

    /// <summary>
    /// Gets the timestamp of the most recent image save, formatted for display.
    /// </summary>
    public string LastAutoSaveFormatted => Disk.LastSaveTime is { } t
        ? Loc.Format("Card.LastAutoSavePrefix", FormatRelativeTime(t))
        : Loc.Get("Card.NeverAutoSaved");

    /// <summary>
    /// Gets the timestamp of the most recent content mutation, formatted for display.
    /// </summary>
    public string LastContentWriteFormatted => Disk.LastContentWriteTime is { } t
        ? Loc.Format("Card.LastWritePrefix", FormatRelativeTime(t))
        : Loc.Get("Card.NeverWritten");

    /// <summary>
    /// Gets the mount point string (e.g., <c>Z:</c>).
    /// </summary>
    public string MountPoint => Disk.MountPoint;

    /// <summary>
    /// Gets the command that opens this disk's backing image file directory in Windows Explorer.
    /// </summary>
    public RelayCommand OpenImageDirectoryCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that opens this disk's mount point in Windows Explorer.
    /// </summary>
    public RelayCommand OpenInExplorerCommand
    {
        get;
    }

    /// <summary>
    /// Gets the capacity (in bytes) that was configured before the automatic raise described
    /// by <see cref="CapacityAdjustedOnLoad"/>, or <c>null</c> if no adjustment occurred.
    /// </summary>
    public ulong? OriginalCapacityBytesOnLoad => Disk.OriginalCapacityBytesOnLoad;

    /// <summary>
    /// Gets the backing image file path, or <c>null</c> if this disk has none.
    /// </summary>
    public string? PersistImagePath => Disk.Options.PersistImagePath;

    /// <summary>
    /// Gets whether this disk has auto-save enabled, controlling visibility of the
    /// last-image-save timestamp on the disk card.
    /// </summary>
    public bool ShowLastAutoSave => Disk.Options.AutoSaveIntervalMinutes is > 0;

    /// <summary>
    /// Gets whether snapshot retention is configured for this disk, controlling visibility
    /// of the "Restore Snapshot" context-menu entry.
    /// </summary>
    public bool SnapshotsEnabled =>
        Disk.Options.MaxSnapshotCount is not null || Disk.Options.MaxSnapshotSizeBytes is not null;

    /// <summary>
    /// Gets this disk's content source for display: its backing image file, or its source
    /// archive if it was mounted via "Import Archive", or <c>null</c> if it has neither.
    /// </summary>
    public string? SourcePath => Disk.Options.SourceArchivePath ?? Disk.Options.PersistImagePath;

    /// <summary>
    /// Gets the amount of used space formatted as a human-readable string.
    /// </summary>
    public string UsedFormatted => ByteFormatter.Format(_usedBytes);

    /// <summary>
    /// Gets the used-space percentage (0–100) for progress-bar display.
    /// </summary>
    public double UsedPercent =>
        Disk.TotalBytes > 0
            ? Math.Round((double)_usedBytes / Disk.TotalBytes * 100.0, 1)
            : 0.0;

    /// <summary>
    /// Gets the volume label from the disk options.
    /// </summary>
    public string VolumeLabel => Disk.Options.VolumeLabel;

    /// <inheritdoc />
    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;
        _activityThrottleTimer.Tick -= OnActivityThrottleTick;
        SetActivityTrackingEnabled(false);
        Disk.SaveFailed -= OnDiskSaveFailed;
    }

    /// <summary>
    /// Refreshes usage statistics, volume label, and capacity immediately. Always recomputes
    /// usage and re-evaluates the high-usage warning (it drives a tray balloon, so it must keep
    /// working while the main window is hidden), but skips the display-only property-change
    /// notifications when the main window isn't visible — nothing is bound to them in that state
    /// (the tray tooltip's own binding is force-refreshed right before it's shown; see
    /// <see cref="MainViewModel"/>).
    /// </summary>
    public void Refresh()
    {
        _usedBytes = Disk.UsedBytes;
        _freeBytes = Disk.FreeBytes;

        var isUiVisible = Application.Current?.MainWindow is { IsVisible: true };
        if (isUiVisible)
        {
            OnPropertyChanged(nameof(UsedFormatted));
            OnPropertyChanged(nameof(FreeFormatted));
            OnPropertyChanged(nameof(FreePercent));
            OnPropertyChanged(nameof(CapacityFormatted));
            OnPropertyChanged(nameof(VolumeLabel));
            OnPropertyChanged(nameof(LastContentWriteFormatted));
            OnPropertyChanged(nameof(LastAutoSaveFormatted));
            OnPropertyChanged(nameof(ShowLastAutoSave));
            OnPropertyChanged(nameof(SnapshotsEnabled));
            OnPropertyChanged(nameof(PersistImagePath));
            OnPropertyChanged(nameof(SourcePath));
            OnPropertyChanged(nameof(HasImagePath));
            OnPropertyChanged(nameof(IsPasswordProtected));

            IsCurrentTempDir = CheckIsCurrentTempDir();
        }

        // Bound by the tray tooltip, which can be visible even while the main window is hidden.
        OnPropertyChanged(nameof(UsedPercent));

        if (Disk.Options.SourceArchivePath != null)
        {
            return;
        }

        var used = UsedPercent;
        if (Disk.Options.HighUsageWarnPercent is not { } threshold)
        {
            if (IsHighUsage)
            {
                IsHighUsage = false;
                OnPropertyChanged(nameof(IsHighUsage));
            }

            return;
        }

        var resetThreshold = Math.Max(1.0, threshold - HighUsageResetGap);
        if (!IsHighUsage && used >= threshold)
        {
            IsHighUsage = true;
            OnPropertyChanged(nameof(IsHighUsage));
            HighUsageWarning?.Invoke(this, EventArgs.Empty);
        }
        else if (IsHighUsage && used < resetThreshold)
        {
            IsHighUsage = false;
            OnPropertyChanged(nameof(IsHighUsage));
        }
    }

    /// <summary>
    /// Enables or disables the <see cref="Disk.ContentAccessed"/> subscription that drives
    /// <see cref="ActivityObserved"/>. The main window's visibility controls this (see
    /// <see cref="App"/>) — no one can see the status bar while it's hidden, so there is no
    /// point paying for dispatcher marshaling and throttle-timer upkeep in that state. Disabling
    /// also stops the throttle timer and discards any pending activity, so a stale snapshot from
    /// before the window was hidden can't surface once tracking resumes.
    /// </summary>
    public void SetActivityTrackingEnabled(bool enabled)
    {
        if (_activityTrackingEnabled == enabled)
        {
            return;
        }

        _activityTrackingEnabled = enabled;
        if (enabled)
        {
            Disk.ContentAccessed += OnContentAccessed;
        }
        else
        {
            Disk.ContentAccessed -= OnContentAccessed;
            _activityThrottleTimer.Stop();
            _pendingActivity = null;
        }
    }

    private static string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var elapsed = DateTimeOffset.UtcNow - timestamp;
        if (elapsed.TotalMinutes < 1)
            return Loc.Get("Time.JustNow");
        if (elapsed.TotalMinutes < 60)
            return Loc.Format("Time.MinutesAgo", (int)elapsed.TotalMinutes);
        if (elapsed.TotalHours < 24)
            return Loc.Format("Time.HoursAgo", (int)elapsed.TotalHours);
        return Loc.Format("Time.DaysAgo", (int)elapsed.TotalDays);
    }

    private bool CheckIsCurrentTempDir()
    {
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        var diskTemp = Path.Combine(MountPoint, "Temp");
        return string.Equals(userTemp, diskTemp, StringComparison.OrdinalIgnoreCase);
    }

    private void OnActivityThrottleTick(object? sender, EventArgs e)
    {
        _activityThrottleTimer.Stop();

        if (_pendingActivity is not { } pending)
        {
            return;
        }

        _pendingActivity = null;
        ActivityObserved?.Invoke(this, pending);
    }

    /// <summary>
    /// Handler for <see cref="RamDisk.ContentAccessed"/>. May run on any WinFsp driver thread,
    /// so the actual throttling/reporting work is dispatched to the UI thread.
    /// </summary>
    private void OnContentAccessed(bool isWrite) =>
        Application.Current?.Dispatcher.BeginInvoke(() => ReportActivity(isWrite));

    private void OnDiskSaveFailed(object? sender, Exception ex) =>
        Application.Current?.Dispatcher.Invoke(() => SaveFailed?.Invoke(this, ex));

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    private void OnRefreshTick(object? sender, EventArgs e) => Refresh();

    /// <summary>
    /// Runs on the UI thread. Implements the leading + trailing throttle: if no report is
    /// in-flight, raises <see cref="ActivityObserved"/> immediately and starts the throttle
    /// window; otherwise just records the latest access, which is reported once the window
    /// elapses (see <see cref="OnActivityThrottleTick"/>). Writes take priority over reads when
    /// both occur within the same window, matching the previous polling behavior.
    /// </summary>
    private void ReportActivity(bool isWrite)
    {
        var access = isWrite ? Disk.LastContentWriteAccess : Disk.LastContentReadAccess;
        if (access is null)
        {
            return;
        }

        var args = new DiskActivityEventArgs(isWrite, access.Path);

        if (!_activityThrottleTimer.IsEnabled)
        {
            _activityThrottleTimer.Start();
            ActivityObserved?.Invoke(this, args);
            return;
        }

        if (isWrite || _pendingActivity is not { IsWrite: true })
        {
            _pendingActivity = args;
        }
    }

    /// <summary>
    /// Event data for <see cref="ActivityObserved"/>.
    /// </summary>
    public sealed class DiskActivityEventArgs(bool isWrite, string filePath) : EventArgs
    {
        /// <summary>
        /// Gets the full path of the file most recently touched.
        /// </summary>
        public string FilePath { get; } = filePath;

        /// <summary>
        /// Gets whether the observed activity was a write (<c>true</c>) or a read (<c>false</c>).
        /// </summary>
        public bool IsWrite { get; } = isWrite;
    }
}