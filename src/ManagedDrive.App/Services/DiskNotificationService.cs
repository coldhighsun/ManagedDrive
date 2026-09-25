using ThrottledLogging;

namespace ManagedDrive.App.Services;

/// <summary>
/// Wires each <see cref="DiskViewModel"/>'s usage/save/activity events to tray balloon tips and
/// status-bar text as disks are added to and removed from <see cref="MainViewModel.Disks"/>.
/// Extracted from <see cref="App"/>'s <c>SetupUsageWarnings</c>.
/// </summary>
public sealed class DiskNotificationService
{
    private readonly HashSet<DiskViewModel> _highUsageDisks = [];
    /// <summary>
    /// Returns whether the main window (and so its status bar) is in front of the user, i.e. shown
    /// and not minimized; see <see cref="WindowVisibility.IsShownToUser(Window?)"/>.
    /// </summary>
    private readonly Func<bool> _isMainWindowShown;
    private readonly ILogger<DiskNotificationService> _logger;
    private readonly MainViewModel _mainViewModel;
    private readonly TrayIconController _trayIconController;

    /// <summary>
    /// Subscribes to <paramref name="mainViewModel"/>'s <see cref="MainViewModel.Disks"/>
    /// collection and wires up every current and future disk.
    /// </summary>
    /// <param name="mainViewModel">The view model owning the disk collection.</param>
    /// <param name="trayIconController">Used to show balloon tips for warnings/failures.</param>
    /// <param name="isMainWindowShown">
    /// Returns whether the main window is shown and not minimized. Decides between reporting a
    /// problem in the status bar only or also with a balloon tip, and sets initial activity
    /// tracking on newly added disks.
    /// </param>
    /// <param name="logger">Used to record throttled high-usage warnings.</param>
    public DiskNotificationService(
        MainViewModel mainViewModel,
        TrayIconController trayIconController,
        Func<bool> isMainWindowShown,
        ILogger<DiskNotificationService> logger)
    {
        _mainViewModel = mainViewModel;
        _trayIconController = trayIconController;
        _isMainWindowShown = isMainWindowShown;
        _logger = logger;

        _mainViewModel.Disks.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (DiskViewModel vm in e.NewItems)
                {
                    vm.HighUsageWarning += OnDiskHighUsageWarning;
                    vm.SaveFailed += OnDiskSaveFailed;
                    vm.SaveCompleted += OnDiskSaveCompleted;
                    vm.ActivityObserved += OnDiskActivityObserved;
                    vm.PropertyChanged += OnDiskPropertyChanged;
                    vm.SetActivityTrackingEnabled(_isMainWindowShown());

                    if (vm is { CapacityAdjustedOnLoad: true, Disk.Options.SourceArchivePath: null })
                    {
                        OnDiskCapacityAdjusted(vm);
                    }

                    if (vm.IsHighUsage)
                    {
                        _highUsageDisks.Add(vm);
                        _trayIconController.SetHighUsageWarningActive(true);
                    }
                }
            }

            if (e.OldItems != null)
            {
                foreach (DiskViewModel vm in e.OldItems)
                {
                    vm.HighUsageWarning -= OnDiskHighUsageWarning;
                    vm.SaveFailed -= OnDiskSaveFailed;
                    vm.SaveCompleted -= OnDiskSaveCompleted;
                    vm.ActivityObserved -= OnDiskActivityObserved;
                    vm.PropertyChanged -= OnDiskPropertyChanged;

                    if (_highUsageDisks.Remove(vm))
                    {
                        _trayIconController.SetHighUsageWarningActive(_highUsageDisks.Count > 0);
                    }

                    if (_mainViewModel.ClearStickyStatus(vm.MountPoint))
                    {
                        ShowRemainingHighUsageStatus();
                    }
                }
            }
        };
    }

    private void OnDiskActivityObserved(object? sender, DiskViewModel.DiskActivityEventArgs e)
    {
        if (sender is not DiskViewModel vm)
        {
            return;
        }

        _mainViewModel.ShowDiskActivityStatus(vm.MountPoint, e.IsWrite, e.FilePath);
    }

    private void OnDiskCapacityAdjusted(DiskViewModel vm)
    {
        var originalMb = vm.OriginalCapacityBytesOnLoad!.Value / (1024 * 1024);
        var newMb = vm.Disk.TotalBytes / (1024 * 1024);

        _mainViewModel.ShowStickyStatus(
            Loc.Format("Status.CapacityAdjusted", vm.MountPoint, originalMb, newMb),
            vm.MountPoint,
            nameof(DiskViewModel.CapacityAdjustedOnLoad));
        ShowBalloonTipUnlessWindowShown(
            Loc.Get("Tray.CapacityAdjustedTitle"),
            Loc.Format("Tray.CapacityAdjustedBody", vm.VolumeLabel, vm.MountPoint, originalMb, newMb),
            System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void OnDiskHighUsageWarning(object? sender, EventArgs e)
    {
        if (sender is not DiskViewModel vm)
        {
            return;
        }

        var body = ShowHighUsageStatus(vm);
        ShowBalloonTipUnlessWindowShown(Loc.Get("Tray.HighUsageTitle"), body, System.Windows.Forms.ToolTipIcon.Warning);

        _logger.LogWarningThrottled(
            $"high-usage:{vm.MountPoint}", TimeSpan.FromMinutes(10),
            "Disk {MountPoint} ({VolumeLabel}) usage reached {UsedPercent:F1}%.",
            vm.MountPoint, vm.VolumeLabel, vm.UsedPercent);
    }

    /// <summary>
    /// Tracks every disk currently in the <see cref="DiskViewModel.IsHighUsage"/> state (which
    /// fires on both the rising and falling edge, unlike the one-shot <see cref="DiskViewModel.HighUsageWarning"/>
    /// event used for the balloon tip below) and reduces it to a single tray blink call: the tray
    /// icon has no per-disk concept, so it only needs to know whether any disk is currently over
    /// its threshold. The falling edge also clears the disk's high-usage report from the status bar,
    /// falling back to the report of another disk that is still over its threshold.
    /// </summary>
    private void OnDiskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DiskViewModel.IsHighUsage) || sender is not DiskViewModel vm)
        {
            return;
        }

        if (vm.IsHighUsage)
        {
            _highUsageDisks.Add(vm);
        }
        else
        {
            _highUsageDisks.Remove(vm);
            if (_mainViewModel.ClearStickyStatus(vm.MountPoint, nameof(DiskViewModel.IsHighUsage)))
            {
                ShowRemainingHighUsageStatus();
            }
        }

        _trayIconController.SetHighUsageWarningActive(_highUsageDisks.Count > 0);
    }

    /// <summary>
    /// Shows <paramref name="vm"/>'s high-usage report as a sticky status.
    /// </summary>
    /// <param name="vm">A disk over its high-usage threshold.</param>
    /// <returns>The report text, also used for the balloon tip.</returns>
    private string ShowHighUsageStatus(DiskViewModel vm)
    {
        var body = Loc.Format("Tray.HighUsageBody", vm.VolumeLabel, vm.MountPoint, vm.UsedPercent);
        _mainViewModel.ShowStickyStatus(body, vm.MountPoint, nameof(DiskViewModel.IsHighUsage));
        return body;
    }

    /// <summary>
    /// After a disk's report was cleared from the status bar, shows the high-usage report of a
    /// disk that is still over its threshold instead of <c>Status.Ready</c>, so a resolved (or
    /// removed) disk doesn't hide another one that is still nearly full.
    /// </summary>
    private void ShowRemainingHighUsageStatus()
    {
        if (_highUsageDisks.FirstOrDefault() is { } remaining)
        {
            ShowHighUsageStatus(remaining);
        }
    }

    /// <summary>
    /// Confirms a user-initiated save (e.g. from the tray menu's "Save Image", where the main
    /// window may be hidden and <see cref="MainViewModel.StatusText"/> not visible to the user)
    /// with a balloon tip, so the action never completes silently. Skipped when the main window
    /// is visible, since <see cref="MainViewModel.StatusText"/> already confirms it there.
    /// </summary>
    private void OnDiskSaveCompleted(object? sender, EventArgs e)
    {
        if (sender is not DiskViewModel vm || _isMainWindowShown())
        {
            return;
        }

        var title = Loc.Get("Tray.SaveCompletedTitle");
        var body = Loc.Format("Tray.SaveCompletedBody", vm.VolumeLabel, vm.MountPoint);
        _trayIconController.ShowBalloonTip(title, body, System.Windows.Forms.ToolTipIcon.Info);
    }

    private void OnDiskSaveFailed(object? sender, Exception ex)
    {
        if (sender is not DiskViewModel vm)
        {
            return;
        }

        _mainViewModel.ShowStickyStatus(
            Loc.Format("Status.SaveFailed", vm.MountPoint, ex.Message),
            vm.MountPoint,
            nameof(DiskViewModel.SaveFailed));
        ShowBalloonTipUnlessWindowShown(
            Loc.Get("Tray.SaveFailedTitle"),
            Loc.Format("Tray.SaveFailedBody", vm.VolumeLabel, vm.MountPoint, ex.Message),
            System.Windows.Forms.ToolTipIcon.Error);
    }

    /// <summary>
    /// Also reports a problem with a balloon tip when the main window's status bar, which already
    /// shows it, isn't in front of the user (window hidden to the tray or minimized).
    /// </summary>
    /// <param name="title">The balloon tip title.</param>
    /// <param name="body">The balloon tip text.</param>
    /// <param name="icon">The balloon tip icon.</param>
    private void ShowBalloonTipUnlessWindowShown(string title, string body, System.Windows.Forms.ToolTipIcon icon)
    {
        if (!_isMainWindowShown())
        {
            _trayIconController.ShowBalloonTip(title, body, icon);
        }
    }
}
