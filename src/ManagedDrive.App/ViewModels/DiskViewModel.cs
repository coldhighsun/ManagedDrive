using ManagedDrive.Core;
using System.ComponentModel;
using System.Windows.Threading;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// View model that wraps a live <see cref="RamDisk"/> and exposes formatted, bindable
/// properties for display in the disk list. Usage statistics are refreshed automatically
/// on a background timer.
/// </summary>
public sealed class DiskViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RamDisk _disk;
    private readonly DispatcherTimer _refreshTimer;

    private ulong _freeBytes;
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

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
    }

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
    /// Gets the mount point string (e.g., <c>Z:</c>).
    /// </summary>
    public string MountPoint => _disk.MountPoint;

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
    /// Refreshes usage statistics immediately.
    /// </summary>
    public void Refresh()
    {
        _usedBytes = _disk.UsedBytes;
        _freeBytes = _disk.FreeBytes;
        OnPropertyChanged(nameof(UsedFormatted));
        OnPropertyChanged(nameof(FreeFormatted));
        OnPropertyChanged(nameof(UsedPercent));
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

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void OnRefreshTick(object? sender, EventArgs e) => Refresh();
}