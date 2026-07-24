namespace ManagedDrive.App.Services;

/// <summary>
/// Wires each <see cref="DiskViewModel"/>'s usage/save/activity events to tray balloon tips and
/// status-bar text as disks are added to and removed from <see cref="MainViewModel.Disks"/>.
/// Extracted from <see cref="App"/>'s <c>SetupUsageWarnings</c>.
/// </summary>
public sealed class DiskNotificationService
{
    private readonly Func<bool> _isMainWindowVisible;
    private readonly MainViewModel _mainViewModel;
    private readonly TrayIconController _trayIconController;

    /// <summary>
    /// Subscribes to <paramref name="mainViewModel"/>'s <see cref="MainViewModel.Disks"/>
    /// collection and wires up every current and future disk.
    /// </summary>
    /// <param name="mainViewModel">The view model owning the disk collection.</param>
    /// <param name="trayIconController">Used to show balloon tips for warnings/failures.</param>
    /// <param name="isMainWindowVisible">Queried to set initial activity tracking on newly added disks.</param>
    public DiskNotificationService(MainViewModel mainViewModel, TrayIconController trayIconController, Func<bool> isMainWindowVisible)
    {
        _mainViewModel = mainViewModel;
        _trayIconController = trayIconController;
        _isMainWindowVisible = isMainWindowVisible;

        _mainViewModel.Disks.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (DiskViewModel vm in e.NewItems)
                {
                    vm.HighUsageWarning += OnDiskHighUsageWarning;
                    vm.SaveFailed += OnDiskSaveFailed;
                    vm.ActivityObserved += OnDiskActivityObserved;
                    vm.SetActivityTrackingEnabled(_isMainWindowVisible());

                    if (vm is { CapacityAdjustedOnLoad: true, Disk.Options.SourceArchivePath: null })
                    {
                        OnDiskCapacityAdjusted(vm);
                    }
                }
            }

            if (e.OldItems != null)
            {
                foreach (DiskViewModel vm in e.OldItems)
                {
                    vm.HighUsageWarning -= OnDiskHighUsageWarning;
                    vm.SaveFailed -= OnDiskSaveFailed;
                    vm.ActivityObserved -= OnDiskActivityObserved;
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

        var title = Loc.Get("Tray.CapacityAdjustedTitle");
        var body = Loc.Format("Tray.CapacityAdjustedBody", vm.VolumeLabel, vm.MountPoint, originalMb, newMb);
        _trayIconController.ShowBalloonTip(title, body, System.Windows.Forms.ToolTipIcon.Warning);

        _mainViewModel.StatusText = Loc.Format("Status.CapacityAdjusted", vm.MountPoint, originalMb, newMb);
    }

    private void OnDiskHighUsageWarning(object? sender, EventArgs e)
    {
        if (sender is not DiskViewModel vm)
        {
            return;
        }

        var title = Loc.Get("Tray.HighUsageTitle");
        var body = Loc.Format("Tray.HighUsageBody", vm.VolumeLabel, vm.MountPoint, vm.UsedPercent);
        _trayIconController.ShowBalloonTip(title, body, System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void OnDiskSaveFailed(object? sender, Exception ex)
    {
        if (sender is not DiskViewModel vm)
        {
            return;
        }

        var title = Loc.Get("Tray.SaveFailedTitle");
        var body = Loc.Format("Tray.SaveFailedBody", vm.VolumeLabel, vm.MountPoint, ex.Message);
        _trayIconController.ShowBalloonTip(title, body, System.Windows.Forms.ToolTipIcon.Error);

        _mainViewModel.StatusText = Loc.Format("Status.SaveFailed", vm.MountPoint, ex.Message);
    }
}