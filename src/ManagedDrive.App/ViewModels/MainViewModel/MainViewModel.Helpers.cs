using static ManagedDrive.App.ViewModels.MainViewModelHelpers;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// Shared private helpers: mounting, target resolution, busy-overlay guards and cleanup.
/// </summary>
public sealed partial class MainViewModel
{
    private static void ResetTempIfPointingAt(string mountPoint)
    {
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        if (!string.IsNullOrEmpty(userTemp))
        {
            var expanded = Environment.ExpandEnvironmentVariables(userTemp);
            if (MountPointValidator.IsPathOnMountPoint(expanded, mountPoint))
            {
                TempDirResetService.Reset();
            }
        }
    }

    /// <summary>
    /// Inserts <paramref name="vm"/> into <see cref="Disks"/> in mount-point order, and stops
    /// keeping any not-mounted saved profile the disk supersedes.
    /// </summary>
    /// <param name="vm">The view model of a disk that was just mounted.</param>
    private void AddDiskSorted(DiskViewModel vm)
    {
        RemoveSupersededProfiles(_unmountedProfiles, ToProfile(vm.Disk.Options));

        var i = 0;
        while (i < Disks.Count &&
               string.Compare(Disks[i].MountPoint, vm.MountPoint, StringComparison.OrdinalIgnoreCase) < 0)
        {
            i++;
        }
        Disks.Insert(i, vm);
    }

    /// <summary>
    /// Deletes a disk's backing <c>.mdr</c> image (plus its snapshots) or source archive file, if
    /// <paramref name="deleteImage"/> is set and the corresponding path is non-null. Shared by the
    /// interactive unmount flow (<see cref="ExecuteUnmount"/>) and the CLI unmount command
    /// (<see cref="UnmountByMountPointAsync"/>).
    /// </summary>
    private async Task DeleteDiskImageIfRequestedAsync(bool deleteImage, string? persistImagePath, string? sourceArchivePath)
    {
        if (deleteImage && persistImagePath != null)
        {
            try
            {
                File.Delete(persistImagePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete image '{Path}'", persistImagePath);
            }
            await Task.Run(() => SnapshotManager.DeleteAllSnapshots(persistImagePath));
        }
        else if (deleteImage && sourceArchivePath != null)
        {
            try
            {
                File.Delete(sourceArchivePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete source archive '{Path}'", sourceArchivePath);
            }
        }
    }

    /// <summary>
    /// Returns whether <paramref name="vm"/> is still in <see cref="Disks"/> and not being
    /// remounted by an edit (its RamDisk is unmounted/disposed then). A modal dialog keeps
    /// pumping the dispatcher, so a CLI command or tray action can unmount the disk while one is
    /// open; a handler that acts on a disk after a dialog (or an await) must re-check, or it would
    /// operate on a disposed disk, or, for unmount/remount, on whatever disk has since been
    /// mounted at the same drive letter. Surfaces the reason via <see cref="StatusText"/>.
    /// </summary>
    /// <param name="vm">The disk the pending action targets.</param>
    /// <returns><c>true</c> if the disk is still mounted; otherwise <c>false</c>.</returns>
    private bool IsStillMounted(DiskViewModel vm)
    {
        if (Disks.Contains(vm) && !vm.IsRemounting)
        {
            return true;
        }

        _logger.LogInformation("Disk {MountPoint} was unmounted while a dialog was open; dropping the pending action.", vm.MountPoint);
        StatusText = Loc.Format("Status.DiskNoLongerMounted", vm.MountPoint);
        return false;
    }

    /// <summary>
    /// Refuses to begin an operation that shows the busy overlay while another one is showing it,
    /// reporting why in the status bar. Checked by those operations' entry points before any
    /// dialog opens, since the tray menu calls <c>Execute</c> directly without checking
    /// <c>CanExecute</c>.
    /// </summary>
    /// <returns><see langword="true"/> if another operation is running and the caller must stop.</returns>
    private bool RejectIfBusy()
    {
        if (!BusyOverlay.IsBusy)
        {
            return false;
        }

        StatusText = Loc.Get("Status.OtherOperationInProgress");
        return true;
    }

    /// <summary>
    /// Shows the busy overlay for a new operation via <see cref="BusyOverlayViewModel.TryStart"/>,
    /// or reports in the status bar that another operation is still running — one may have
    /// started (e.g. from the CLI) while this operation's dialogs were open.
    /// </summary>
    /// <param name="statusText">Status text to display above the progress bar.</param>
    /// <param name="indeterminate">Whether the operation has no computable total.</param>
    /// <param name="totalBytes">Total byte count for the operation, if known.</param>
    /// <param name="cancellationSource">The operation's cancellation source, if it can be cancelled.</param>
    /// <returns>
    /// <see langword="true"/> if the overlay now shows this operation, which must stop it when
    /// done; <see langword="false"/> if the caller must not run.
    /// </returns>
    private bool TryStartBusyOverlay(
        string statusText, bool indeterminate = false, ulong? totalBytes = null, CancellationTokenSource? cancellationSource = null)
    {
        if (BusyOverlay.TryStart(statusText, indeterminate, totalBytes, cancellationSource))
        {
            return true;
        }

        _logger.LogInformation("Refused to start '{StatusText}' while another operation is running", statusText);
        StatusText = Loc.Get("Status.OtherOperationInProgress");
        return false;
    }

    /// <summary>
    /// Resolves the disk a command targets (its parameter, else <see cref="SelectedDisk"/>), or
    /// <c>null</c> when that disk is being remounted by an edit — its RamDisk is already
    /// unmounted/disposed then. Used by both the execute and can-execute side of every per-disk
    /// command, since the tray menu calls <c>Execute</c> directly without checking <c>CanExecute</c>.
    /// </summary>
    /// <param name="parameter">The command parameter.</param>
    /// <returns>The target disk, or <c>null</c> if there is none or it is being remounted.</returns>
    private DiskViewModel? ResolveTarget(object? parameter) =>
        (parameter as DiskViewModel ?? SelectedDisk) is { IsRemounting: false } vm ? vm : null;

    /// <summary>
    /// Finds the disk mounted at <paramref name="mountPoint"/> for a CLI request, skipping one
    /// being remounted by an edit (see <see cref="ResolveTarget"/>).
    /// </summary>
    /// <param name="mountPoint">The drive letter (e.g. <c>R:</c>) to look up.</param>
    /// <returns>The matching disk, or <c>null</c> if none is available.</returns>
    private DiskViewModel? FindDisk(string mountPoint) =>
        Disks.FirstOrDefault(d => !d.IsRemounting && string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the <see cref="DiskOptions"/> of every currently active disk except
    /// <paramref name="excluding"/>. Used to validate that a new or edited disk's image file
    /// path does not collide with another disk's mount point or image file.
    /// </summary>
    private IReadOnlyList<DiskOptions> GetOtherDiskOptions(DiskViewModel? excluding) =>
        Disks.Where(d => d != excluding).Select(d => d.Disk.Options).ToList();

    private async Task MountAndAddAsync(DiskOptions options, string? password = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var disk = await MountWithPasswordRetryAsync(options, password, progress, cancellationToken);
            if (disk is null)
            {
                StatusText = Loc.Get("Status.MountFailed");
                return;
            }

            AddDiskSorted(new(disk));
            SaveSettings();
            StatusText = Loc.Format("Status.MountedWithCapacity", disk.MountPoint, options.VolumeLabel, options.CapacityBytes / (1024 * 1024));
            _logger.LogInformation("Disk mounted: {MountPoint}, label {VolumeLabel}.", disk.MountPoint, options.VolumeLabel);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Get("Status.OperationCancelled");
            _logger.LogInformation("Mount cancelled for {MountPoint}.", options.MountPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mount failed for {MountPoint}.", options.MountPoint);
            ShowError(Loc.Format("Msg.MountFailed", ex.Message));
            StatusText = Loc.Get("Status.MountFailed");
        }
    }

    /// <summary>
    /// Mounts <paramref name="options"/>, prompting for a password via <see cref="PasswordPromptDialog"/>
    /// and retrying whenever the image is encrypted and the supplied password is missing or wrong.
    /// </summary>
    /// <returns>The mounted disk, or <c>null</c> if the user cancelled the password prompt.</returns>
    /// <exception cref="Exception">Any mount failure other than a password issue propagates to the caller.</exception>
    private async Task<RamDisk?> MountWithPasswordRetryAsync(DiskOptions options, string? password = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            string? errorMessage;
            try
            {
                return await Task.Run(() => _mountManager.Mount(options, password, progress, cancellationToken));
            }
            catch (ImagePasswordRequiredException)
            {
                errorMessage = null;
            }
            catch (ImagePasswordIncorrectException)
            {
                errorMessage = Loc.Get("Val.PasswordIncorrect");
            }

            var prompt = new PasswordPromptDialog(Loc.Get("PasswordPrompt.Title"), errorMessage, options);
            if (Application.Current.MainWindow is { IsVisible: true } mainWindow)
            {
                // Owner must already have been shown (e.g. not yet true when starting minimized,
                // as during startup auto-mount), or WPF throws.
                prompt.Owner = mainWindow;
            }

            if (prompt.ShowDialog() != true)
            {
                return null;
            }

            password = prompt.Password;
        }
    }
}
