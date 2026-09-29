using static ManagedDrive.App.ViewModels.MainViewModelHelpers;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// WPF command handlers for snapshot-related actions and save-with-snapshot.
/// </summary>
public sealed partial class MainViewModel
{
    private async void ExecuteRestoreSnapshot(DiskViewModel? vm)
    {
        if (vm == null || vm.Disk.Options.PersistImagePath is not { } imagePath)
        {
            return;
        }

        var snapshots = await Task.Run(() => SnapshotManager.ListSnapshots(imagePath));
        if (!IsStillMounted(vm))
        {
            return;
        }

        if (snapshots.Count == 0)
        {
            ShowInfo(Loc.Get("Msg.NoSnapshotsAvailable"));
            return;
        }

        var dialog = new RestoreSnapshotDialog(vm, snapshots)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true || dialog.SelectedSnapshotPath is not { } selectedPath || !IsStillMounted(vm))
        {
            return;
        }

        var confirm = new ConfirmDialog(
            Loc.Get("Msg.RestoreSnapshotConfirmTitle"),
            Loc.Format("Msg.RestoreSnapshotConfirmBody", vm.MountPoint, vm.VolumeLabel, dialog.SelectedSnapshotLabel))
        {
            Owner = Application.Current.MainWindow
        };

        if (confirm.ShowDialog() != true || !IsStillMounted(vm))
        {
            return;
        }

        _logger.LogInformation("Restore snapshot confirmed for {MountPoint}: {SnapshotPath}.", vm.MountPoint, selectedPath);
        string? error = null;
        var success = await Task.Run(() => vm.Disk.TryRestoreFromSnapshot(selectedPath, out error));

        if (!success)
        {
            _logger.LogWarning("Restore snapshot failed for {MountPoint}: {Error}", vm.MountPoint, error);
            ShowWarning(error);
            return;
        }

        vm.Refresh();
        StatusText = Loc.Format("Status.SnapshotRestored", vm.MountPoint);
        _logger.LogInformation("Restore snapshot completed for {MountPoint}.", vm.MountPoint);
    }

    /// <summary>
    /// Writes a timestamped snapshot for <paramref name="vm"/> right now, via the same
    /// <see cref="RamDisk.SaveToImageWithSnapshot"/> call the regular "Save Image" action uses —
    /// snapshot creation always piggybacks on a full image save, there's no lighter-weight path.
    /// </summary>
    private async void ExecuteCreateSnapshotNow(DiskViewModel? vm)
    {
        if (vm is not { SnapshotsEnabled: true, HasImagePath: true } || RejectIfBusy())
        {
            return;
        }

        _logger.LogInformation("Create snapshot now requested for {MountPoint}.", vm.MountPoint);
        await SaveImageWithSnapshotAsync(vm, Loc.Get("Busy.CreatingSnapshot"), "Create snapshot now");
    }

    /// <summary>
    /// Shared body for <see cref="ExecuteSaveImage"/> and <see cref="ExecuteCreateSnapshotNow"/>:
    /// runs <see cref="RamDisk.SaveToImageWithSnapshot"/> under the busy overlay (with
    /// cancellation), and reports the outcome via <see cref="StatusText"/>/logging.
    /// </summary>
    /// <param name="vm">The disk to save.</param>
    /// <param name="busyText">The busy-overlay status text shown while the save runs.</param>
    /// <param name="logVerb">The action name used in log messages (e.g. <c>"Save image"</c>).</param>
    private async Task SaveImageWithSnapshotAsync(DiskViewModel vm, string busyText, string logVerb)
    {
        using var cts = new CancellationTokenSource();
        if (!TryStartBusyOverlay(busyText, totalBytes: vm.Disk.UsedBytes, cancellationSource: cts))
        {
            return;
        }

        vm.IsSaving = true;
        try
        {
            var progress = new Progress<double>(BusyOverlay.Report);
            await Task.Run(() => vm.Disk.SaveToImageWithSnapshot(progress, cts.Token));
            StatusText = Loc.Format("Status.ImageSaved", vm.MountPoint);
            _logger.LogInformation("{Verb} completed for {MountPoint}.", logVerb, vm.MountPoint);
            vm.NotifySaveCompleted();
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Get("Status.OperationCancelled");
            _logger.LogInformation("{Verb} cancelled for {MountPoint}.", logVerb, vm.MountPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Verb} failed for {MountPoint}.", logVerb, vm.MountPoint);
            ShowError(Loc.Format("Msg.SaveImageFailed", ex.Message));
        }
        finally
        {
            vm.IsSaving = false;
            BusyOverlay.Stop();
        }
    }
}
