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

    private readonly DispatcherTimer _refreshTimer;

    private ulong _freeBytes;
    private bool _isCurrentTempDir;
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

        Disk.SaveFailed += OnDiskSaveFailed;
    }

    /// <summary>
    /// Occurs when disk usage reaches or exceeds the configured high-usage warning threshold.
    /// </summary>
    public event EventHandler? HighUsageWarning;

    /// <summary>
    /// Occurs when an image save or snapshot write fails for this disk. Always raised on the
    /// UI dispatcher thread, since the underlying <see cref="RamDisk.SaveFailed"/> event may
    /// originate from a background auto-save timer thread.
    /// </summary>
    public event EventHandler<Exception>? SaveFailed;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets whether this disk's effective capacity was automatically raised at mount time
    /// because the loaded image's actual content exceeded the configured capacity. This is a
    /// one-time fact determined during mount, not an ongoing event.
    /// </summary>
    public bool CapacityAdjustedOnLoad => Disk.OriginalCapacityBytesOnLoad.HasValue;

    /// <summary>
    /// Gets the capacity (in bytes) that was configured before the automatic raise described
    /// by <see cref="CapacityAdjustedOnLoad"/>, or <c>null</c> if no adjustment occurred.
    /// </summary>
    public ulong? OriginalCapacityBytesOnLoad => Disk.OriginalCapacityBytesOnLoad;

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
    /// Gets whether this disk is read-only.
    /// </summary>
    public bool IsReadOnly => Disk.Options.ReadOnly;

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
    /// Gets the backing image file path, or <c>null</c> if this disk has none.
    /// </summary>
    public string? PersistImagePath => Disk.Options.PersistImagePath;

    /// <summary>
    /// Gets this disk's content source for display: its backing image file, or its source
    /// archive if it was mounted via "Import Archive", or <c>null</c> if it has neither.
    /// </summary>
    public string? SourcePath => Disk.Options.SourceArchivePath ?? Disk.Options.PersistImagePath;

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
        Disk.SaveFailed -= OnDiskSaveFailed;
    }

    /// <summary>
    /// Refreshes usage statistics, volume label, and capacity immediately.
    /// </summary>
    public void Refresh()
    {
        _usedBytes = Disk.UsedBytes;
        _freeBytes = Disk.FreeBytes;
        OnPropertyChanged(nameof(UsedFormatted));
        OnPropertyChanged(nameof(FreeFormatted));
        OnPropertyChanged(nameof(FreePercent));
        OnPropertyChanged(nameof(UsedPercent));
        OnPropertyChanged(nameof(CapacityFormatted));
        OnPropertyChanged(nameof(VolumeLabel));
        OnPropertyChanged(nameof(LastContentWriteFormatted));
        OnPropertyChanged(nameof(LastAutoSaveFormatted));
        OnPropertyChanged(nameof(ShowLastAutoSave));
        OnPropertyChanged(nameof(SnapshotsEnabled));
        OnPropertyChanged(nameof(PersistImagePath));
        OnPropertyChanged(nameof(SourcePath));
        OnPropertyChanged(nameof(HasImagePath));

        IsCurrentTempDir = CheckIsCurrentTempDir();

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

    private void OnDiskSaveFailed(object? sender, Exception ex) =>
        Application.Current?.Dispatcher.Invoke(() => SaveFailed?.Invoke(this, ex));

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    private void OnRefreshTick(object? sender, EventArgs e) => Refresh();
}