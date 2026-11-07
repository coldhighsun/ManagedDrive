using static ManagedDrive.App.ViewModels.MainViewModelHelpers;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// WPF command handlers for snapshot-related actions and save-with-snapshot.
/// </summary>
public sealed partial class MainViewModel
{
    private async Task ExecuteRestoreSnapshotAsync(DiskViewModel? vm)
    {
        if (vm == null || vm.Disk.Options.PersistImagePath is not { } imagePath)
        {
            return;
        }

        var snapshots = await Task.Run(() => SnapshotManager.ListSnapshotsNewestFirst(imagePath));
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
    /// Body of <see cref="ExecuteSaveImageAsync"/>: runs <see cref="RamDisk.SaveToImageWithSnapshot"/>
    /// under the busy overlay (with cancellation), and reports the outcome via
    /// <see cref="StatusText"/>/logging. A snapshot is written afterwards only when snapshot
    /// retention is configured; there is no separate manual snapshot action.
    /// </summary>
    /// <param name="vm">The disk to save.</param>
    private async Task SaveImageWithSnapshotAsync(DiskViewModel vm)
    {
        using var cts = new CancellationTokenSource();
        if (!TryStartBusyOverlay(Loc.Get("Busy.SavingImage"), totalBytes: vm.Disk.UsedBytes, cancellationSource: cts))
        {
            return;
        }

        vm.IsSaving = true;
        try
        {
            var progress = new Progress<double>(BusyOverlay.Report);
            await Task.Run(() => vm.Disk.SaveToImageWithSnapshot(progress, cts.Token));
            StatusText = Loc.Format("Status.ImageSaved", vm.MountPoint);
            _logger.LogInformation("Save image completed for {MountPoint}.", vm.MountPoint);
            vm.NotifySaveCompleted();
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Get("Status.OperationCancelled");
            _logger.LogInformation("Save image cancelled for {MountPoint}.", vm.MountPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save image failed for {MountPoint}.", vm.MountPoint);
            ShowError(Loc.Format("Msg.SaveImageFailed", ex.Message));
        }
        finally
        {
            vm.IsSaving = false;
            BusyOverlay.Stop();
        }
    }
}
