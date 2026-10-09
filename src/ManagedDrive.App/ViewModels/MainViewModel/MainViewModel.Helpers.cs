using static ManagedDrive.App.ViewModels.MainViewModelHelpers;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// Shared private helpers: mounting, target resolution, busy-overlay guards and cleanup.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// The variables the temp preset redirects.
    /// </summary>
    private static readonly string[] TempVariables = ["TEMP", "TMP"];

    /// <summary>
    /// Puts the user's TEMP and TMP back to what they held before they were pointed into a RAM
    /// disk. When nothing was recorded (a setting made by an older version) they are reset to the
    /// Windows defaults instead. Call it only while TEMP points into a RAM disk.
    /// </summary>
    /// <param name="broadcast">Whether to announce the change to running programs.</param>
    /// <returns><c>true</c> on success; <c>false</c> if the Windows defaults could not be written.</returns>
    public bool RestoreUserTemp(bool broadcast = true) =>
        _envRedirector.RestoreVariables(TempVariables, broadcast) > 0 || TempDirResetService.Reset(broadcast);

    /// <summary>
    /// Puts TEMP and TMP back for an explicit "restore" request from the user, who may not have a RAM
    /// disk as TEMP at all: what was recorded is restored; without a record the Windows defaults are
    /// written only when TEMP really points into a mounted RAM disk, so a TEMP the user chose
    /// themselves is never overwritten.
    /// </summary>
    /// <returns>What was done.</returns>
    public EnvRestoreResult RestoreRecordedTemp()
    {
        if (_envRedirector.RestoreVariables(TempVariables) > 0)
        {
            return EnvRestoreResult.Restored;
        }

        // Disks is bound to the UI, so it is only read on the UI thread; this method runs on a pool thread.
        var dispatcher = Application.Current.Dispatcher;
        var onKnownDisk = dispatcher.CheckAccess() ? IsTempOnKnownDisk() : dispatcher.Invoke(IsTempOnKnownDisk);
        if (!onKnownDisk)
        {
            return EnvRestoreResult.NothingToRestore;
        }

        return TempDirResetService.Reset() ? EnvRestoreResult.Restored : EnvRestoreResult.Failed;
    }

    /// <summary>
    /// Whether the user's TEMP points into a mounted RAM disk or into a disk of a saved profile, so a
    /// TEMP left pointing at a disk that is not mounted any more (set by an older version, no backup)
    /// is still recognised as ours. Must run on the UI thread.
    /// </summary>
    /// <returns><c>true</c> when TEMP is on one of the app's disks.</returns>
    private bool IsTempOnKnownDisk()
    {
        if (TempDirCompatChecker.IsTempOnAnyDisk(Disks))
        {
            return true;
        }

        // The profiles kept in memory are the saved ones that are not mounted, so settings.json need not be read.
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        return !string.IsNullOrEmpty(userTemp) &&
            TempDirCompatChecker.FindProfileContainingPath(Environment.ExpandEnvironmentVariables(userTemp), _unmountedProfiles) is not null;
    }

    /// <summary>
    /// Restores TEMP and TMP when TEMP points into the disk at <paramref name="mountPoint"/>.
    /// </summary>
    /// <param name="mountPoint">The mount point of a disk that is not available.</param>
    private void ResetTempIfPointingAt(string mountPoint)
    {
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        if (!string.IsNullOrEmpty(userTemp))
        {
            var expanded = Environment.ExpandEnvironmentVariables(userTemp);
            if (MountPointValidator.IsPathOnMountPoint(expanded, mountPoint))
            {
                RestoreUserTemp();
            }
        }
    }

    /// <summary>
    /// Asks the user, once, to accept that a RAM disk as TEMP can break installers run by a
    /// system-level service. The first showing is remembered in the settings.
    /// </summary>
    /// <param name="owner">The window the confirmation belongs to.</param>
    /// <returns><c>true</c> when the warning was already shown or the user accepts it.</returns>
    public bool ConfirmTempDirWarning(Window owner)
    {
        if (_settingsStore.Load().TempDirCompatWarningShown)
        {
            return true;
        }

        var warn = new ConfirmDialog(
            Loc.Get("Msg.SetTempDirWarningTitle"),
            Loc.Get("Msg.SetTempDirWarningBody"))
        {
            Owner = owner
        };
        if (warn.ShowDialog() != true)
        {
            return false;
        }

        _settingsStore.Update(current => current with { TempDirCompatWarningShown = true });
        return true;
    }

    /// <summary>
    /// Disposes <paramref name="vm"/>, removes it from <see cref="Disks"/> and unmounts its disk,
    /// which performs the final save. The disk's own <c>SaveFailed</c> event is observed directly
    /// because disposing the view model detaches the UI subscribers before that save runs.
    /// </summary>
    /// <param name="vm">The view model of the disk to unmount.</param>
    /// <returns>The exception that made the final save fail, or <c>null</c> when it succeeded.</returns>
    private async Task<Exception?> UnmountDiskAsync(DiskViewModel vm)
    {
        var mountPoint = vm.Disk.Options.MountPoint;
        var disk = vm.Disk;
        Exception? saveError = null;

        void OnSaveFailed(object? sender, Exception ex) => saveError = ex;

        disk.SaveFailed += OnSaveFailed;
        (RamDisk Disk, Task Task)? pending = null;
        try
        {
            vm.Dispose();

            // The disk leaves Disks before its final save finishes; the pending entry keeps it
            // counted for path-in-use checks and lets shutdown wait for the save.
            var unmountTask = Task.Run(() => _mountManager.Unmount(mountPoint));
            pending = (disk, unmountTask);
            _pendingUnmounts.Add(pending.Value);
            Disks.Remove(vm);
            await unmountTask;
        }
        finally
        {
            disk.SaveFailed -= OnSaveFailed;
            if (pending is { } entry)
            {
                _pendingUnmounts.Remove(entry);
            }
        }

        return saveError;
    }

    /// <summary>
    /// Gets a value indicating whether a disk that already left <see cref="Disks"/> is still
    /// running its final save.
    /// </summary>
    internal bool HasPendingUnmounts => _pendingUnmounts.Count > 0;

    /// <summary>
    /// Gets the disks that already left <see cref="Disks"/> but are still running their final
    /// save, so shutdown can watch them for save failures too.
    /// </summary>
    internal IReadOnlyList<RamDisk> PendingUnmountDisks => _pendingUnmounts.Select(p => p.Disk).ToList();

    /// <summary>
    /// Completes when every unmount started by <see cref="UnmountDiskAsync"/> has finished its
    /// final save. Never throws: a failed unmount is already reported to the caller that started it.
    /// </summary>
    /// <returns>A task that completes when the pending unmounts are done.</returns>
    internal async Task WaitForPendingUnmountsAsync()
    {
        foreach (var (_, task) in _pendingUnmounts.ToList())
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A pending unmount failed while waiting for it during shutdown.");
            }
        }
    }

    /// <summary>
    /// Reports the outcome of an unmount: a sticky error when the final save failed (so the loss
    /// is not hidden behind an "Unmounted" message), the regular status otherwise.
    /// </summary>
    /// <param name="mountPoint">The mount point that was unmounted.</param>
    /// <param name="saveError">The final-save failure returned by <see cref="UnmountDiskAsync"/>.</param>
    private void ShowUnmountResult(string mountPoint, Exception? saveError)
    {
        if (saveError is null)
        {
            StatusText = Loc.Format("Status.Unmounted", mountPoint);
            return;
        }

        ShowStickyStatus(
            Loc.Format("Status.SaveFailed", mountPoint, saveError.Message),
            mountPoint,
            nameof(DiskViewModel.SaveFailed));
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
        _disksAcceptingEffects[vm.Disk] = 0;
        Disks.Insert(i, vm);
        _ = ApplyDiskEffectsAsync(vm.Disk);
    }

    /// <summary>
    /// Creates a freshly mounted disk's folders and points its environment variables into it.
    /// Does nothing for a disk that has neither. Reports anything that could not be done in the
    /// status line; the disk itself is unaffected.
    /// </summary>
    /// <param name="disk">The mounted disk.</param>
    /// <returns>A task completing when the effects are applied.</returns>
    private async Task ApplyDiskEffectsAsync(RamDisk disk)
    {
        var options = disk.Options;
        if (options.Folders is not { Count: > 0 } && options.EnvRedirects is not { Count: > 0 })
        {
            return;
        }

        try
        {
            var result = await Task.Run(() => _envRedirector.ApplyDiskEffects(options, () => _disksAcceptingEffects.ContainsKey(disk)));
            if (!result.IsComplete)
            {
                _logger.LogWarning(
                    "Preset effects on {MountPoint} incomplete. Rejected: {Rejected}. Failed: {Failed}.",
                    options.MountPoint, string.Join(", ", result.Rejected), string.Join(", ", result.Failed));
                ShowStickyStatus(Loc.Format("Status.PresetEffectsIncomplete", options.MountPoint, string.Join(", ", result.Rejected.Concat(result.Failed))));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying preset effects on {MountPoint} failed.", options.MountPoint);
        }
    }

    /// <summary>
    /// Returns a disk's options with the presets that redirect variables decided by the user's
    /// environment: the edit dialog ticks them from what the variables really point at, not from
    /// what the saved settings say.
    /// </summary>
    /// <param name="options">The disk's options.</param>
    /// <returns>The options with matching folders and redirections.</returns>
    private DiskOptions WithEnvironmentPresets(DiskOptions options)
    {
        var effective = PresetSelection.Reconcile(
            BuiltInPresets.All,
            options.Folders ?? [],
            options.EnvRedirects ?? [],
            redirect => _envRedirector.PointsInto(options.MountPoint, redirect));
        return options with
        {
            Folders = effective.Folders.Count == 0 ? null : effective.Folders,
            EnvRedirects = effective.EnvRedirects.Count == 0 ? null : effective.EnvRedirects,
        };
    }

    /// <summary>
    /// Takes the presets that redirect environment variables away from every other disk when a
    /// disk is created or edited to have them: such a preset can be on one disk only, so the disk
    /// the user just chose wins and the others lose it (their variables are put back, their folders
    /// stay). Presets that only create folders are left alone. Disks that are not mounted lose them
    /// from their saved profile.
    /// </summary>
    /// <param name="newOptions">The options of the disk being created or edited.</param>
    /// <param name="oldOptions">The disk's options before the edit, or <c>null</c> for a new disk.</param>
    /// <param name="excluding">The disk being edited, which is not one of the "other" disks.</param>
    /// <returns>A sentence saying what moved, or <c>null</c> when nothing did.</returns>
    private async Task<string?> ReleaseClaimedPresetsAsync(DiskOptions newOptions, DiskOptions? oldOptions, DiskViewModel? excluding)
    {
        // This runs before a mount or an edit that has already locked the disk (IsRemounting); a failure
        // here must not abort that flow, the disks just keep what they had.
        try
        {
            return await ReleaseClaimedPresetsCoreAsync(newOptions, oldOptions, excluding);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Taking presets away from other disks failed for {MountPoint}.", newOptions.MountPoint);
            return null;
        }
    }

    /// <summary>
    /// Whether a mounted disk can have exclusive presets taken away: it must really apply presets
    /// (a read-only disk does not, even when its options still list them) and not be the disk that
    /// is being created or edited.
    /// </summary>
    /// <param name="other">The other disk's options.</param>
    /// <param name="newMountPoint">The mount point of the disk being created or edited.</param>
    /// <returns><c>true</c> if the disk may lose presets.</returns>
    internal static bool CanLosePresets(DiskOptions other, string newMountPoint) =>
        other.AppliesPresets() && !string.Equals(other.MountPoint, newMountPoint, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a saved, not mounted profile can have exclusive presets taken away; see
    /// <see cref="CanLosePresets(DiskOptions, string)"/>.
    /// </summary>
    /// <param name="profile">The saved profile.</param>
    /// <param name="newMountPoint">The mount point of the disk being created or edited.</param>
    /// <returns><c>true</c> if the profile may lose presets.</returns>
    internal static bool CanLosePresets(DiskProfile profile, string newMountPoint) =>
        profile.AppliesPresets() && !string.Equals(profile.MountPoint, newMountPoint, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Does the work of <see cref="ReleaseClaimedPresetsAsync"/>; may throw.
    /// </summary>
    /// <param name="newOptions">The options of the disk being created or edited.</param>
    /// <param name="oldOptions">The disk's options before the edit, or <c>null</c> for a new disk.</param>
    /// <param name="excluding">The disk being edited, which is not one of the "other" disks.</param>
    /// <returns>A sentence saying what moved, or <c>null</c> when nothing did.</returns>
    private async Task<string?> ReleaseClaimedPresetsCoreAsync(DiskOptions newOptions, DiskOptions? oldOptions, DiskViewModel? excluding)
    {
        var all = BuiltInPresets.All;
        var before = oldOptions is null
            ? []
            : PresetSelection.Detect(all, oldOptions.Folders ?? [], oldOptions.EnvRedirects ?? []);
        var now = PresetSelection.Detect(all, newOptions.Folders ?? [], newOptions.EnvRedirects ?? []);
        var claimed = all
            .Where(preset => PresetSelection.IsExclusive(preset) && now.Contains(preset.Id) && !before.Contains(preset.Id))
            .ToList();
        if (claimed.Count == 0)
        {
            return null;
        }

        var notes = new List<string>();
        // Read-only disks and profiles never applied their presets, so they do not own any variable.
        foreach (var other in Disks.Where(d => !ReferenceEquals(d, excluding) &&
            CanLosePresets(d.Disk.Options, newOptions.MountPoint)).ToList())
        {
            var options = other.Disk.Options;
            var release = PresetSelection.Release(all, options.Folders ?? [], options.EnvRedirects ?? [], claimed);
            if (release.IsEmpty)
            {
                continue;
            }

            var error = await ApplyOptionsAsync(
                other.Disk,
                WithEffects(options, release.Folders, release.EnvRedirects),
                () => _envRedirector.RestoreVariables(release.ReleasedVariables, mountPoint: options.MountPoint));
            if (error is not null)
            {
                _logger.LogWarning("Taking presets away from {MountPoint} failed: {Error}", options.MountPoint, error);
                continue;
            }

            other.Refresh();
            notes.Add(DescribeMovedPresets(release.ReleasedPresetIds, options.MountPoint, newOptions.MountPoint));
        }

        for (var i = 0; i < _unmountedProfiles.Count; i++)
        {
            var profile = _unmountedProfiles[i];
            if (!CanLosePresets(profile, newOptions.MountPoint))
            {
                continue;
            }

            // Saved profiles keep the variable-redirecting presets as ids, so look at the full list.
            var expanded = PresetSelection.Expand(all, profile.PresetIds ?? [], profile.Folders ?? [], profile.EnvRedirects ?? []);
            var release = PresetSelection.Release(all, expanded.Folders, expanded.EnvRedirects, claimed);
            if (release.IsEmpty)
            {
                continue;
            }

            var stored = PresetSelection.Split(all, release.Folders, release.EnvRedirects);
            _unmountedProfiles[i] = profile with
            {
                PresetIds = stored.PresetIds.Count == 0 ? null : stored.PresetIds,
                Folders = stored.Folders.Count == 0 ? null : stored.Folders,
                EnvRedirects = stored.EnvRedirects.Count == 0 ? null : stored.EnvRedirects,
            };
            notes.Add(DescribeMovedPresets(release.ReleasedPresetIds, profile.MountPoint, newOptions.MountPoint));
        }

        if (notes.Count == 0)
        {
            return null;
        }

        SaveSettings();
        var message = string.Join(" ", notes);
        _logger.LogInformation("{Message}", message);
        return message;
    }

    /// <summary>
    /// Applies new options to a mounted disk off the UI thread.
    /// </summary>
    /// <param name="disk">The mounted disk.</param>
    /// <param name="options">The options to apply.</param>
    /// <param name="afterApplied">Run on the same background thread once the options were applied; skipped when they were refused.</param>
    /// <returns>The reason the options were refused, or <c>null</c> when they were applied.</returns>
    private static Task<string?> ApplyOptionsAsync(RamDisk disk, DiskOptions options, Action? afterApplied = null) =>
        Task.Run<string?>(() =>
        {
            if (!disk.TryApplyOptions(options, out var error))
            {
                return error;
            }

            afterApplied?.Invoke();
            return null;
        });

    /// <summary>
    /// Copies options with new folders and redirections, storing empty lists as <c>null</c> the way
    /// the rest of the options do.
    /// </summary>
    /// <param name="options">The options to copy.</param>
    /// <param name="folders">The folders the disk should have.</param>
    /// <param name="redirects">The redirections the disk should have.</param>
    /// <returns>The new options.</returns>
    private static DiskOptions WithEffects(DiskOptions options, IReadOnlyList<string> folders, IReadOnlyList<EnvRedirect> redirects) =>
        options with
        {
            Folders = folders.Count == 0 ? null : folders,
            EnvRedirects = redirects.Count == 0 ? null : redirects,
        };

    /// <summary>
    /// Says that presets moved from one disk to another.
    /// </summary>
    /// <param name="presetIds">The ids of the presets.</param>
    /// <param name="from">The mount point that lost them.</param>
    /// <param name="to">The mount point that has them now.</param>
    /// <returns>The sentence.</returns>
    private static string DescribeMovedPresets(IReadOnlyList<string> presetIds, string from, string to) =>
        Loc.Format("Status.PresetMoved", string.Join(", ", presetIds.Select(id => Loc.Get($"Preset.{id}.Name"))), from, to);

    /// <summary>
    /// Brings a mounted disk's folders and environment variables in line with an edit that did not
    /// remount it: new folders are created, new or changed variables are pointed into the disk and
    /// variables that are no longer wanted are put back. Folders that are no longer wanted stay on
    /// the disk, since they may hold cache data. Reports anything that could not be done in the
    /// status line; the disk itself is unaffected.
    /// </summary>
    /// <param name="disk">The mounted disk, already carrying the new options.</param>
    /// <param name="oldOptions">The options before the edit.</param>
    /// <param name="newOptions">The options after the edit.</param>
    /// <returns>A task completing when the changes are applied.</returns>
    private async Task SyncDiskEffectsAsync(RamDisk disk, DiskOptions oldOptions, DiskOptions newOptions)
    {
        var change = DiskEffectsDiff.Compute(oldOptions.Folders, oldOptions.EnvRedirects, newOptions.Folders, newOptions.EnvRedirects);
        if (change.IsEmpty)
        {
            return;
        }

        try
        {
            var result = await Task.Run(() =>
            {
                _envRedirector.RestoreVariables(change.RemovedVariables, mountPoint: newOptions.MountPoint);

                // A TEMP set by an older version has no backup to restore from; it would keep pointing
                // into the disk, so it goes back to the Windows defaults instead.
                if ((oldOptions.EnvRedirects ?? []).Any(redirect =>
                        change.RemovedVariables.Contains(redirect.Variable, StringComparer.OrdinalIgnoreCase) &&
                        TempVariables.Contains(redirect.Variable, StringComparer.OrdinalIgnoreCase) &&
                        _envRedirector.PointsInto(newOptions.MountPoint, redirect)))
                {
                    TempDirResetService.Reset();
                }

                return _envRedirector.ApplyDiskEffects(
                    newOptions with { Folders = change.AddedFolders, EnvRedirects = change.AddedRedirects },
                    () => _disksAcceptingEffects.ContainsKey(disk));
            });
            if (!result.IsComplete)
            {
                _logger.LogWarning(
                    "Preset effects on {MountPoint} incomplete after an edit. Rejected: {Rejected}. Failed: {Failed}.",
                    newOptions.MountPoint, string.Join(", ", result.Rejected), string.Join(", ", result.Failed));
                ShowStickyStatus(Loc.Format("Status.PresetEffectsIncomplete", newOptions.MountPoint, string.Join(", ", result.Rejected.Concat(result.Failed))));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying preset effects on {MountPoint} after an edit failed.", newOptions.MountPoint);
        }
    }

    /// <summary>
    /// Puts back the environment variables that pointed into a disk when it leaves
    /// <see cref="Disks"/>, however it was unmounted.
    /// </summary>
    /// <param name="sender">The disk list.</param>
    /// <param name="e">What changed.</param>
    private void OnDisksChangedRestoreEnvRedirects(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            _disksAcceptingEffects.Clear();
            RestoreDanglingEnvRedirects();
            return;
        }

        foreach (var removed in e.OldItems?.OfType<DiskViewModel>() ?? [])
        {
            // First, so an apply still queued for this disk skips instead of running after the restore.
            _disksAcceptingEffects.TryRemove(removed.Disk, out _);
            _envRedirector.Restore(removed.MountPoint);
        }
    }

    /// <summary>
    /// Puts back every environment variable that points into a RAM disk. Called when the app
    /// exits, before the disks are torn down.
    /// </summary>
    /// <param name="broadcast">
    /// Whether to announce the change to running programs; <c>false</c> when the session is ending.
    /// </param>
    public void RestoreAllEnvRedirects(bool broadcast = true) => _envRedirector.RestoreAll(broadcast);

    /// <summary>
    /// Puts back the environment variables that point into a disk that is not mounted — what a
    /// crash leaves behind. Called at startup before the auto-mount, when no disk is mounted yet.
    /// </summary>
    public void RestoreDanglingEnvRedirects() =>
        _envRedirector.RestoreDangling(mountPoint =>
            Disks.Any(disk => string.Equals(disk.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase)));

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
        Disks.Where(d => d != excluding).Select(d => d.Disk.Options)
            .Concat(_pendingUnmounts.Select(p => p.Disk.Options))
            .ToList();

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
        var attempt = await TryMountOnceAsync(options, password, progress, cancellationToken);
        return attempt.Disk ?? await PromptForPasswordAndMountAsync(options, attempt.PasswordError, progress, cancellationToken);
    }

    /// <summary>
    /// Mounts <paramref name="options"/> once on the thread pool without ever prompting, so
    /// several disks can be loaded at the same time and any password prompts held back until the
    /// loading is done. A missing or wrong password is reported in the result instead of thrown.
    /// </summary>
    /// <param name="options">The disk to mount.</param>
    /// <param name="password">The image password, if one is known.</param>
    /// <param name="progress">An optional progress reporter.</param>
    /// <param name="cancellationToken">Token to cancel a slow load.</param>
    /// <returns>The mounted disk, or the reason it needs a password.</returns>
    /// <exception cref="Exception">Any mount failure other than a password issue propagates.</exception>
    private async Task<MountAttempt> TryMountOnceAsync(DiskOptions options, string? password, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var disk = await Task.Run(() => _mountManager.Mount(options, password, progress, cancellationToken));
            return new(disk, null);
        }
        catch (ImagePasswordRequiredException)
        {
            return new(null, null);
        }
        catch (ImagePasswordIncorrectException)
        {
            return new(null, Loc.Get("Val.PasswordIncorrect"));
        }
    }

    /// <summary>
    /// Outcome of <see cref="TryMountOnceAsync"/>.
    /// </summary>
    /// <param name="Disk">The mounted disk, or <c>null</c> if the image needs a password.</param>
    /// <param name="PasswordError">
    /// The message to show in the password prompt when a password was tried and rejected, or
    /// <c>null</c> when none was given yet.
    /// </param>
    /// <param name="Error">
    /// The failure that stopped the mount, set only by callers that catch it instead of letting
    /// it propagate (see <see cref="TryMountFromProfileAsync"/>).
    /// </param>
    internal sealed record MountAttempt(RamDisk? Disk, string? PasswordError, Exception? Error = null)
    {
        /// <summary>
        /// Gets a value indicating whether the mount stopped only because the image needs a
        /// (correct) password: neither mounted nor failed.
        /// </summary>
        public bool NeedsPassword => Disk is null && Error is null;
    }

    /// <summary>
    /// Asks for the image password with <see cref="PasswordPromptDialog"/> and retries the mount
    /// until it succeeds, a non-password error occurs, or the user cancels.
    /// </summary>
    /// <param name="options">The disk to mount.</param>
    /// <param name="errorMessage">Shown in the first prompt; <c>null</c> for none.</param>
    /// <param name="progress">An optional progress reporter.</param>
    /// <param name="cancellationToken">Token to cancel a slow load.</param>
    /// <returns>The mounted disk, or <c>null</c> if the user cancelled the password prompt.</returns>
    private async Task<RamDisk?> PromptForPasswordAndMountAsync(DiskOptions options, string? errorMessage, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        while (true)
        {
            var prompt = new PasswordPromptDialog(Loc.Get("PasswordPrompt.Title"), errorMessage, options);
            if (Application.Current.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized } mainWindow)
            {
                // Owner must already have been shown (e.g. not yet true when starting minimized,
                // as during startup auto-mount), or WPF throws.
                prompt.Owner = mainWindow;
            }

            if (prompt.ShowDialog() != true)
            {
                return null;
            }

            var attempt = await TryMountOnceAsync(options, prompt.Password, progress, cancellationToken);
            if (attempt.Disk is not null)
            {
                return attempt.Disk;
            }

            errorMessage = attempt.PasswordError;
        }
    }
}
