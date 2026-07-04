namespace ManagedDrive.App.ViewModels;

/// <summary>
/// View model that wraps a live <see cref="RamDisk"/> and exposes formatted, bindable
/// properties for display in the disk list. Usage statistics are refreshed automatically
/// on a background timer.
/// </summary>
public sealed class DiskViewModel : INotifyPropertyChanged, IDisposable
{
    private const double HighUsageResetThreshold = 85.0;
    private const double HighUsageThreshold = 90.0;
    private readonly RamDisk _disk;
    private readonly DispatcherTimer _refreshTimer;

    private ulong _freeBytes;
    private bool _highUsageWarned;
    private bool _isCurrentTempDir;
    private ulong _usedBytes;

    /// <summary>
    /// Initializes a new view model for <paramref name="disk"/>.
    /// </summary>
    /// <param name="disk">The live RAM disk to represent.</param>
    public DiskViewModel(RamDisk disk)
    {
        _disk = disk;
        _usedBytes = disk.UsedBytes;
        _freeBytes = disk.FreeBytes;
        _isCurrentTempDir = CheckIsCurrentTempDir();

        OpenInExplorerCommand = new(_ => Process.Start("explorer.exe", MountPoint));

        _refreshTimer = new()
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
    }

    /// <summary>
    /// Raised when disk usage first crosses the 90% threshold.
    /// Resets (and can re-fire) only after usage drops below 85%.
    /// </summary>
    public event EventHandler? HighUsageWarning;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the total capacity formatted as a human-readable string.
    /// </summary>
    public string CapacityFormatted => FormatBytes(_disk.TotalBytes);

    /// <summary>
    /// Gets the underlying <see cref="RamDisk"/> instance.
    /// </summary>
    public RamDisk Disk => _disk;

    /// <summary>
    /// Gets the amount of free space formatted as a human-readable string.
    /// </summary>
    public string FreeFormatted => FormatBytes(_freeBytes);

    /// <summary>
    /// Gets the free-space percentage (0–100) for display.
    /// </summary>
    public double FreePercent =>
        _disk.TotalBytes > 0
            ? Math.Round((double)_freeBytes / _disk.TotalBytes * 100.0, 1)
            : 100.0;

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
    /// Gets the inverse of <see cref="IsCurrentTempDir"/> for visibility binding.
    /// </summary>
    public bool IsNotCurrentTempDir => !_isCurrentTempDir;

    /// <summary>
    /// Gets the timestamp of the most recent image save, formatted for display.
    /// </summary>
    public string LastAutoSaveFormatted => _disk.LastSaveTime is { } t
        ? Loc.Format("Card.LastAutoSavePrefix", t.ToLocalTime().ToString("HH:mm:ss"))
        : Loc.Get("Card.NeverAutoSaved");

    /// <summary>
    /// Gets the timestamp of the most recent content mutation, formatted for display.
    /// </summary>
    public string LastContentWriteFormatted => _disk.LastContentWriteTime is { } t
        ? Loc.Format("Card.LastWritePrefix", t.ToLocalTime().ToString("HH:mm:ss"))
        : Loc.Get("Card.NeverWritten");

    /// <summary>
    /// Gets the mount point string (e.g., <c>Z:</c>).
    /// </summary>
    public string MountPoint => _disk.MountPoint;

    /// <summary>
    /// Gets the command that opens this disk's mount point in Windows Explorer.
    /// </summary>
    public RelayCommand OpenInExplorerCommand
    {
        get;
    }

    /// <summary>
    /// Gets whether this disk has auto-save enabled, controlling visibility of the
    /// last-image-save timestamp on the disk card.
    /// </summary>
    public bool ShowLastAutoSave => _disk.Options.AutoSaveIntervalMinutes is > 0;

    /// <summary>
    /// Gets the amount of used space formatted as a human-readable string.
    /// </summary>
    public string UsedFormatted => FormatBytes(_usedBytes);

    /// <summary>
    /// Gets the used-space percentage (0–100) for progress-bar display.
    /// </summary>
    public double UsedPercent =>
        _disk.TotalBytes > 0
            ? Math.Round((double)_usedBytes / _disk.TotalBytes * 100.0, 1)
            : 0.0;

    /// <summary>
    /// Gets the volume label from the disk options.
    /// </summary>
    public string VolumeLabel => _disk.Options.VolumeLabel;

    /// <inheritdoc />
    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;
    }

    /// <summary>
    /// Refreshes usage statistics, volume label, and capacity immediately.
    /// </summary>
    public void Refresh()
    {
        _usedBytes = _disk.UsedBytes;
        _freeBytes = _disk.FreeBytes;
        OnPropertyChanged(nameof(UsedFormatted));
        OnPropertyChanged(nameof(FreeFormatted));
        OnPropertyChanged(nameof(FreePercent));
        OnPropertyChanged(nameof(UsedPercent));
        OnPropertyChanged(nameof(CapacityFormatted));
        OnPropertyChanged(nameof(VolumeLabel));
        OnPropertyChanged(nameof(LastContentWriteFormatted));
        OnPropertyChanged(nameof(LastAutoSaveFormatted));
        OnPropertyChanged(nameof(ShowLastAutoSave));

        IsCurrentTempDir = CheckIsCurrentTempDir();

        var used = UsedPercent;
        if (!_highUsageWarned && used >= HighUsageThreshold)
        {
            _highUsageWarned = true;
            HighUsageWarning?.Invoke(this, EventArgs.Empty);
        }
        else if (_highUsageWarned && used < HighUsageResetThreshold)
        {
            _highUsageWarned = false;
        }
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        if (bytes >= 1024UL * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        if (bytes >= 1024UL)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        return $"{bytes} B";
    }

    private bool CheckIsCurrentTempDir()
    {
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        var diskTemp = Path.Combine(MountPoint, "Temp");
        return string.Equals(userTemp, diskTemp, StringComparison.OrdinalIgnoreCase);
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    private void OnRefreshTick(object? sender, EventArgs e) => Refresh();
}