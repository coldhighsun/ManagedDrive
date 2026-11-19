using ManagedDrive.Cli.Core;
using ThrottledLogging;

namespace ManagedDrive.App.Services;

/// <summary>
/// Wires each <see cref="DiskViewModel"/>'s usage/save/activity/write-rejection events, and the
/// system's available memory, to tray balloon tips, the tray icon's alert badge and status-bar text
/// as disks are added to and removed from <see cref="MainViewModel.Disks"/>.
/// Extracted from <see cref="App"/>'s <c>SetupUsageWarnings</c>.
/// </summary>
public sealed class DiskNotificationService
{
    /// <summary>
    /// How long a refused-write balloon tip stays quiet for the same disk and reason. A copy that
    /// hits a full disk fails again on every retry, and one balloon per attempt would bury it.
    /// </summary>
    private static readonly TimeSpan WriteRejectedCooldown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often available system memory is checked. Runs whether or not the main window is
    /// visible: the balloon tip it can raise exists for when the window is hidden.
    /// </summary>
    private static readonly TimeSpan LowMemoryPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The problem a refused write's sticky status reports, so <see cref="MainViewModel.ClearStickyStatus"/>
    /// can remove it once memory recovers.
    /// </summary>
    private const string WriteRejectedProblem = "WriteRejected";

    /// <summary>
    /// Stands in for a mount point on the sticky status of the system-wide low-memory warning,
    /// which belongs to no single disk.
    /// </summary>
    private const string LowMemoryStatusKey = "system-memory";

    /// <summary>
    /// The disks over their high-usage threshold, kept to show another one's report in the status
    /// bar once one of them recovers.
    /// </summary>
    private readonly HashSet<DiskViewModel> _highUsageDisks = [];

    /// <summary>
    /// Decides when each disk's tray alert reasons start and end, and the badge level they add up to.
    /// </summary>
    private readonly TrayAlertTracker _alerts = new();
    private readonly DispatcherTimer _lowMemoryTimer;
    private readonly LowMemoryMonitor _lowMemoryMonitor = new();
    private readonly NotificationCooldown _writeRejectedCooldown = new(WriteRejectedCooldown);
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
                    vm.SaveRecovered += OnDiskSaveRecovered;
                    vm.WriteRejected += OnDiskWriteRejected;
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
                        ApplyAlerts(_alerts.SetHighUsage(vm, true));
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
                    vm.SaveRecovered -= OnDiskSaveRecovered;
                    vm.WriteRejected -= OnDiskWriteRejected;
                    vm.ActivityObserved -= OnDiskActivityObserved;
                    vm.PropertyChanged -= OnDiskPropertyChanged;

                    _highUsageDisks.Remove(vm);
                    ApplyAlerts(_alerts.RemoveDisk(vm, _mainViewModel.Disks.Count));

                    if (_mainViewModel.ClearStickyStatus(vm.MountPoint))
                    {
                        ShowRemainingHighUsageStatus();
                    }
                }
            }
        };

        _lowMemoryTimer = new()
        {
            Interval = LowMemoryPollInterval
        };
        _lowMemoryTimer.Tick += OnLowMemoryTick;
        _lowMemoryTimer.Start();
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
            System.Windows.Forms.ToolTipIcon.Warning,
            NotificationClickTarget.ForDisk(DiskNotificationKind.CapacityAdjusted, vm.MountPoint));
    }

    private void OnDiskHighUsageWarning(object? sender, EventArgs e)
    {
        if (sender is not DiskViewModel vm)
        {
            return;
        }

        var body = ShowHighUsageStatus(vm);
        ShowBalloonTipUnlessWindowShown(
            Loc.Get("Tray.HighUsageTitle"),
            body,
            System.Windows.Forms.ToolTipIcon.Warning,
            NotificationClickTarget.ForDisk(DiskNotificationKind.HighUsage, vm.MountPoint));

        _logger.LogWarningThrottled(
            $"high-usage:{vm.MountPoint}", TimeSpan.FromMinutes(10),
            "Disk {MountPoint} ({VolumeLabel}) usage reached {UsedPercent:F1}%.",
            vm.MountPoint, vm.VolumeLabel, vm.UsedPercent);
    }

    /// <summary>
    /// Follows a change of <see cref="DiskViewModel.IsHighUsage"/> (both edges, unlike the one-shot
    /// <see cref="DiskViewModel.HighUsageWarning"/> event used for the balloon tip): sets or clears the
    /// disk's high-usage tray warning, and on the falling edge clears the disk's high-usage report from
    /// the status bar, falling back to the report of another disk that is still over its threshold.
    /// </summary>
    /// <param name="sender">The disk.</param>
    /// <param name="e">The changed property.</param>
    private void OnDiskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DiskViewModel.IsHighUsage) || sender is not DiskViewModel vm)
        {
            return;
        }

        if (vm.IsHighUsage)
        {
            _highUsageDisks.Add(vm);

            // The one-shot warning (balloon) is rate-limited and may not fire for this rise, but the status bar must
            // still say the disk is nearly full while the card shows the warning.
            ShowHighUsageStatus(vm);
        }
        else
        {
            _highUsageDisks.Remove(vm);
            if (_mainViewModel.ClearStickyStatus(vm.MountPoint, nameof(DiskViewModel.IsHighUsage)))
            {
                ShowRemainingHighUsageStatus();
            }
        }

        ApplyAlerts(_alerts.SetHighUsage(vm, vm.IsHighUsage));
    }

    /// <summary>
    /// Applies what <see cref="_alerts"/> changed: mirrors each changed disk's reasons on its view
    /// model for the tooltip, and passes the badge level to the tray icon, asking it to pulse again
    /// for a new failure.
    /// </summary>
    /// <param name="update">The change to apply.</param>
    private void ApplyAlerts(TrayAlertUpdate update)
    {
        if (!update.HasChanges)
        {
            return;
        }

        foreach (var key in update.ChangedKeys)
        {
            if (key is DiskViewModel vm)
            {
                vm.SetAlertReasons(_alerts.Get(vm));
            }
        }

        _trayIconController.SetAlertLevel(_alerts.Level, update.NewCritical);
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
        _trayIconController.ShowBalloonTip(
            title,
            body,
            System.Windows.Forms.ToolTipIcon.Info,
            NotificationClickTarget.ForDisk(DiskNotificationKind.SaveCompleted, vm.MountPoint));
    }

    private void OnDiskSaveFailed(object? sender, Exception ex)
    {
        if (sender is not DiskViewModel vm)
        {
            return;
        }

        ApplyAlerts(_alerts.OnSaveFailed(vm));
        _mainViewModel.ShowStickyStatus(
            Loc.Format("Status.SaveFailed", vm.MountPoint, ex.Message),
            vm.MountPoint,
            nameof(DiskViewModel.SaveFailed));
        ShowBalloonTipUnlessWindowShown(
            Loc.Get("Tray.SaveFailedTitle"),
            Loc.Format("Tray.SaveFailedBody", vm.VolumeLabel, vm.MountPoint, ex.Message),
            System.Windows.Forms.ToolTipIcon.Error,
            NotificationClickTarget.ForDisk(DiskNotificationKind.SaveFailed, vm.MountPoint));
    }

    /// <summary>
    /// Clears the sticky save-failure report once a save succeeds again, and says so with a
    /// balloon tip when the main window isn't in front, since that is where the failure was
    /// reported too.
    /// </summary>
    private void OnDiskSaveRecovered(object? sender, EventArgs e)
    {
        if (sender is not DiskViewModel vm)
        {
            return;
        }

        ApplyAlerts(_alerts.OnSaveRecovered(vm));
        if (_mainViewModel.ClearStickyStatus(vm.MountPoint, nameof(DiskViewModel.SaveFailed)))
        {
            ShowRemainingHighUsageStatus();
        }

        ShowBalloonTipUnlessWindowShown(
            Loc.Get("Tray.SaveRecoveredTitle"),
            Loc.Format("Tray.SaveRecoveredBody", vm.VolumeLabel, vm.MountPoint),
            System.Windows.Forms.ToolTipIcon.Info,
            NotificationClickTarget.ForDisk(DiskNotificationKind.SaveRecovered, vm.MountPoint));
    }

    /// <summary>
    /// Tells the user why a write was refused. The application that issued it only gets an error
    /// code, so without this a failed copy gives no hint that the disk is full or memory is low.
    /// Raises a critical tray alert for <see cref="TrayAlertTracker.RefusalHold"/> after the last refusal.
    /// A low-memory refusal stays in the status bar until memory recovers; a full disk is already
    /// visible in the usage bar, so it only replaces the status text. The balloon tip is limited to
    /// one per disk and reason per <see cref="WriteRejectedCooldown"/>, and is only spent when it
    /// is actually shown.
    /// </summary>
    private void OnDiskWriteRejected(object? sender, WriteRejectionReason reason)
    {
        if (sender is not DiskViewModel vm)
        {
            return;
        }

        var isLowMemory = reason == WriteRejectionReason.LowMemory;
        ApplyAlerts(_alerts.OnWriteRejected(vm, isLowMemory, DateTimeOffset.UtcNow));

        var status = Loc.Format(isLowMemory ? "Status.WriteRejectedMemory" : "Status.WriteRejectedFull", vm.MountPoint);
        if (isLowMemory)
        {
            _mainViewModel.ShowStickyStatus(status, vm.MountPoint, WriteRejectedProblem);
        }
        else
        {
            _mainViewModel.ShowTransientStatus(status);
        }

        _logger.LogWarningThrottled(
            $"write-rejected:{vm.MountPoint}:{reason}", WriteRejectedCooldown,
            "Disk {MountPoint} ({VolumeLabel}) refused a write: {Reason}.",
            vm.MountPoint, vm.VolumeLabel, reason);

        if (_isMainWindowShown() || !_writeRejectedCooldown.TryAcquire($"{vm.MountPoint}|{reason}"))
        {
            return;
        }

        _trayIconController.ShowBalloonTip(
            Loc.Get(isLowMemory ? "Tray.WriteRejectedMemoryTitle" : "Tray.WriteRejectedFullTitle"),
            Loc.Format(isLowMemory ? "Tray.WriteRejectedMemoryBody" : "Tray.WriteRejectedFullBody", vm.VolumeLabel, vm.MountPoint),
            System.Windows.Forms.ToolTipIcon.Warning,
            NotificationClickTarget.ForWriteRejection(reason, vm.MountPoint));
    }

    /// <summary>
    /// Checks available system memory and warns when it gets low, ahead of the RAM disks' own
    /// guard refusing writes, or clears the refused-write reports once it has recovered. Does
    /// nothing while no disk is mounted, since then no RAM disk contributes to the pressure.
    /// </summary>
    private void OnLowMemoryTick(object? sender, EventArgs e)
    {
        var available = SystemMemoryInfo.GetAvailablePhysicalBytes();
        var cleared = false;
        switch (_lowMemoryMonitor.Evaluate(available, DateTimeOffset.UtcNow))
        {
            case LowMemoryTransition.Warn when _mainViewModel.Disks.Count > 0:
                var formatted = ByteFormatter.Format(available);
                _mainViewModel.ShowStickyStatus(Loc.Format("Status.LowMemory", formatted), LowMemoryStatusKey, nameof(LowMemoryMonitor));
                ShowBalloonTipUnlessWindowShown(
                    Loc.Get("Tray.LowMemoryTitle"),
                    Loc.Format("Tray.LowMemoryBody", formatted),
                    System.Windows.Forms.ToolTipIcon.Warning,
                    NotificationClickTarget.ShowWindow);
                _logger.LogWarning("Available system memory is down to {Available}.", formatted);
                break;

            case LowMemoryTransition.Recovered:
                cleared = _mainViewModel.ClearStickyStatus(LowMemoryStatusKey);
                break;
        }

        // The tray warning follows the reading itself, not the monitor's transitions, which its
        // cooldown holds back; the same poll ends refusal alerts whose hold has passed.
        ApplyAlerts(_alerts.OnMemoryReading(available, _mainViewModel.Disks.Count, DateTimeOffset.UtcNow));

        // Independent of the monitor's state: a refusal can come and go between two polls, so the
        // monitor never saw memory low and reports no recovery.
        if (available > LowMemoryMonitor.RecoverAboveBytes)
        {
            foreach (var vm in _mainViewModel.Disks)
            {
                cleared |= _mainViewModel.ClearStickyStatus(vm.MountPoint, WriteRejectedProblem);
            }
        }

        if (cleared)
        {
            ShowRemainingHighUsageStatus();
        }
    }

    /// <summary>
    /// Also reports a problem with a balloon tip when the main window's status bar, which already
    /// shows it, isn't in front of the user (window hidden to the tray or minimized).
    /// </summary>
    /// <param name="title">The balloon tip title.</param>
    /// <param name="body">The balloon tip text.</param>
    /// <param name="icon">The balloon tip icon.</param>
    /// <param name="clickTarget">Where clicking the balloon tip leads.</param>
    private void ShowBalloonTipUnlessWindowShown(
        string title,
        string body,
        System.Windows.Forms.ToolTipIcon icon,
        NotificationClickTarget clickTarget)
    {
        if (!_isMainWindowShown())
        {
            _trayIconController.ShowBalloonTip(title, body, icon, clickTarget);
        }
    }
}
