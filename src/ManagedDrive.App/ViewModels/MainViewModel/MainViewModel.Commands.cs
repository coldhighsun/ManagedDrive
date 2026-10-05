using System.Windows.Input;
using static ManagedDrive.App.ViewModels.MainViewModelHelpers;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// WPF command handlers (toolbar, context menu, import/export flows).
/// </summary>
public sealed partial class MainViewModel
{
    private void ExecuteAbout()
    {
        _logger.LogInformation("About dialog opened.");

        var dialog = new AboutDialog(UpdateCheckService);
        if (Application.Current.MainWindow is { IsLoaded: true } mainWindow)
        {
            dialog.Owner = mainWindow;
        }

        dialog.ShowDialog();
    }

    private async void ExecuteCloneDisk(DiskViewModel? vm)
    {
        if (vm == null || RejectIfBusy())
        {
            return;
        }

        var targets = Disks.Where(d => d != vm && !d.IsReadOnly && !d.IsRemounting).ToList();

        // Include the source disk's own options (excluding: null) so exporting to a path that
        // the source itself is already persisting to is also rejected — that file may be
        // concurrently written by the source's auto-save timer.
        var dialog = new CloneDiskDialog(vm, targets, GetOtherDiskOptions(excluding: null))
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true || !IsStillMounted(vm))
        {
            return;
        }

        if (dialog.TargetDisk is { } target)
        {
            var confirm = new ConfirmDialog(
                Loc.Get("Msg.CloneDiskConfirmTitle"),
                Loc.Format("Msg.CloneDiskConfirmBody", vm.MountPoint, target.MountPoint, target.VolumeLabel))
            {
                Owner = Application.Current.MainWindow
            };

            if (confirm.ShowDialog() != true || !IsStillMounted(vm) || !IsStillMounted(target))
            {
                return;
            }

            // Copying every file's content is memory-bound work that scales with the disk's used
            // size, so it runs off the UI thread behind the busy overlay (which also stops another
            // long operation from starting meanwhile).
            if (!TryStartBusyOverlay(Loc.Get("Busy.CloningDisk"), indeterminate: true))
            {
                return;
            }

            bool cloned;
            string? error;
            try
            {
                (cloned, error) = await Task.Run(() =>
                {
                    var ok = target.Disk.TryCloneFrom(vm.Disk, out var cloneError);
                    return (ok, cloneError);
                });
            }
            finally
            {
                BusyOverlay.Stop();
            }

            if (!cloned)
            {
                _logger.LogWarning("Clone disk failed: {Source} -> {Target}: {Error}", vm.MountPoint, target.MountPoint, error);
                ShowWarning(error);
                return;
            }

            // The target may have been unmounted while the clone ran off the UI thread.
            if (!IsStillMounted(target))
            {
                return;
            }

            target.Refresh();
            StatusText = Loc.Format("Status.DiskCloned", vm.MountPoint, target.MountPoint);
            _logger.LogInformation("Disk cloned: {Source} -> {Target}.", vm.MountPoint, target.MountPoint);
        }
        else if (dialog.ExportPath is { } exportPath)
        {
            _logger.LogInformation("Disk export requested: {Source} -> {ExportPath}.", vm.MountPoint, exportPath);
            using var cts = new CancellationTokenSource();
            if (!TryStartBusyOverlay(Loc.Get("Busy.ExportingImage"), totalBytes: vm.Disk.UsedBytes, cancellationSource: cts))
            {
                return;
            }

            try
            {
                var progress = new Progress<double>(BusyOverlay.Report);
                if (dialog.ExportArchiveFormat is { } archiveFormat)
                {
                    await Task.Run(() => vm.Disk.ExportToArchive(exportPath, archiveFormat, dialog.ExportCompressionLevel, progress, cts.Token));
                }
                else
                {
                    await Task.Run(() => vm.Disk.ExportToImage(exportPath, dialog.ExportCompressionLevel, progress: progress, cancellationToken: cts.Token));
                }
                StatusText = Loc.Format("Status.DiskExported", vm.MountPoint, exportPath);
                _logger.LogInformation("Disk export completed: {Source} -> {ExportPath}.", vm.MountPoint, exportPath);
            }
            catch (OperationCanceledException)
            {
                StatusText = Loc.Get("Status.OperationCancelled");
                _logger.LogInformation("Disk export cancelled: {Source} -> {ExportPath}.", vm.MountPoint, exportPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Disk export failed: {Source} -> {ExportPath}.", vm.MountPoint, exportPath);
                ShowError(Loc.Format("Msg.SaveImageFailed", ex.Message));
            }
            finally
            {
                BusyOverlay.Stop();
            }
        }
    }

    private async void ExecuteCreateDisk()
    {
        var config = _settingsStore.Load();
        var dialog = new CreateDiskDialog(
            GetOtherDiskOptions(excluding: null),
            config.DefaultCompressionLevel,
            config.DefaultImageDirectory)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _logger.LogInformation("Create disk requested: {MountPoint}, capacity {CapacityBytes} bytes.",
            dialog.Result!.MountPoint, dialog.Result!.CapacityBytes);
        await MountAndAddAsync(dialog.Result!, dialog.PasswordChanged ? dialog.Password : null);
    }

    private async void ExecuteEditDisk(DiskViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        var dialog = new CreateDiskDialog(vm.Disk.Options, GetOtherDiskOptions(excluding: vm), vm.Disk.CurrentPassword)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true || !IsStillMounted(vm))
        {
            return;
        }

        var newOptions = dialog.Result!;
        var old = vm.Disk.Options;
        var needsRemount = newOptions.MountPoint != old.MountPoint || newOptions.ReadOnly != old.ReadOnly;

        _logger.LogInformation("Edit disk requested: {MountPoint} (remount: {NeedsRemount}).", vm.MountPoint, needsRemount);

        if (dialog.PasswordChanged && dialog.Password is null && vm.Disk.IsPasswordProtected)
        {
            var confirmRemove = new ConfirmDialog(
                Loc.Get("Msg.RemovePasswordConfirmTitle"),
                Loc.Get("Msg.RemovePasswordConfirmBody"))
            {
                Owner = Application.Current.MainWindow
            };

            if (confirmRemove.ShowDialog() != true || !IsStillMounted(vm))
            {
                return;
            }

            _logger.LogInformation("Password removal confirmed for disk {MountPoint}.", vm.MountPoint);
        }
        else if (dialog.PasswordChanged && dialog.Password is not null && !vm.Disk.IsPasswordProtected &&
            newOptions.PersistImagePath is { } imagePath &&
            (await Task.Run(() => SnapshotManager.ListSnapshots(imagePath))).Count > 0)
        {
            // RamDisk.SetPassword deletes the existing plaintext snapshots when encryption is added.
            var confirmAdd = new ConfirmDialog(
                Loc.Get("Msg.AddPasswordConfirmTitle"),
                Loc.Get("Msg.AddPasswordConfirmBody"))
            {
                Owner = Application.Current.MainWindow
            };

            if (confirmAdd.ShowDialog() != true || !IsStillMounted(vm))
            {
                return;
            }

            _logger.LogInformation("Password addition confirmed for disk {MountPoint}.", vm.MountPoint);
        }
        else if (dialog.PasswordChanged)
        {
            _logger.LogInformation("Password changed for disk {MountPoint}.", vm.MountPoint);
        }

        if (needsRemount)
        {
            var body = Loc.Format("Msg.EditDiskConfirmBody", vm.MountPoint, vm.VolumeLabel);
            if (vm.IsCurrentTempDir)
            {
                body += "\n\n" + Loc.Get("Msg.TempDirWillBeReset");
            }

            var confirm = new ConfirmDialog(Loc.Get("Msg.EditDiskConfirmTitle"), body)
            {
                Owner = Application.Current.MainWindow
            };

            if (confirm.ShowDialog() != true || !IsStillMounted(vm))
            {
                return;
            }

            // From here on the disk is out of reach of every other command and CLI request (see
            // ResolveTarget/FindDisk/IsStillMounted): its RamDisk is about to be disposed, and a
            // second edit racing this one would otherwise mount the same image twice.
            vm.IsRemounting = true;
            CommandManager.InvalidateRequerySuggested();

            if (vm.IsCurrentTempDir)
            {
                await Task.Run(TempDirResetService.Reset);
            }

            var currentPassword = vm.Disk.CurrentPassword;
            var oldMountPoint = old.MountPoint;

            try
            {
                // Inside the try: MountManager.Unmount drops the disk from its list before
                // disposing it, so a throw here leaves it just as unmounted as a Mount failure.
                await Task.Run(() => _mountManager.Unmount(oldMountPoint));

                // A password failure must not escape the lambda: the disk is already mounted by
                // then, and the catch below would drop it from Disks while it stays mounted.
                string? passwordError = null;
                var disk = await Task.Run(() =>
                {
                    var mounted = _mountManager.Mount(newOptions, currentPassword);
                    if (dialog.PasswordChanged)
                    {
                        passwordError = TrySetPassword(mounted, dialog.Password);
                    }
                    return mounted;
                });

                vm.Dispose();
                Disks.Remove(vm);
                AddDiskSorted(new(disk));
                SaveSettings();
                StatusText = Loc.Format("Status.MountedWithCapacity", disk.MountPoint, newOptions.VolumeLabel, newOptions.CapacityBytes / (1024 * 1024));
                if (passwordError != null)
                {
                    ShowError(passwordError);
                }
                _logger.LogInformation("Edit disk remount succeeded: {OldMountPoint} -> {NewMountPoint}.", oldMountPoint, disk.MountPoint);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Edit disk remount failed: {OldMountPoint} -> {NewMountPoint}.", oldMountPoint, newOptions.MountPoint);
                vm.Dispose();
                Disks.Remove(vm);

                // Keep the disk's profile (with its pre-edit options, which still match the image
                // on disk — the new drive letter may be the very reason the mount failed) so it is
                // not silently dropped from the settings the next time they are saved.
                RetainSavedProfiles([ToProfile(old)]);
                SaveSettings();
                ShowError(Loc.Format("Msg.MountFailed", ex.Message));
                StatusText = Loc.Get("Status.MountFailed");
            }
        }
        else
        {
            // Kept apart from the apply error: once the options are applied live they must be
            // refreshed and saved even if the password change then fails.
            string? passwordError = null;
            var error = await Task.Run(() =>
            {
                if (!vm.Disk.TryApplyOptions(newOptions, out var applyError))
                {
                    return applyError;
                }

                if (dialog.PasswordChanged)
                {
                    passwordError = TrySetPassword(vm.Disk, dialog.Password);
                }

                return null;
            });

            if (error != null)
            {
                _logger.LogWarning("Edit disk failed for {MountPoint}: {Error}", vm.MountPoint, error);
                MessageBox.Show(
                    error,
                    Loc.Get("Msg.EditDiskConfirmTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            vm.Refresh();
            SaveSettings();
            StatusText = Loc.Format("Status.MountedWithCapacity", vm.MountPoint, newOptions.VolumeLabel, newOptions.CapacityBytes / (1024 * 1024));
            if (passwordError != null)
            {
                ShowError(passwordError);
            }
            _logger.LogInformation("Edit disk applied live for {MountPoint}.", vm.MountPoint);
        }
    }

    private void ExecuteExit()
    {
        _logger.LogInformation("Exit requested from UI.");

        if (Disks.Count == 0)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var tempOnRamDisk = TempDirCompatChecker.IsTempOnAnyDisk(Disks);

        var body = Loc.Get("Msg.ExitConfirmBody");
        if (tempOnRamDisk)
        {
            body = body + "\n\n" + Loc.Get("Msg.ExitTempDirWillBeReset");
        }

        var dialog = new ConfirmDialog(
            Loc.Get("Msg.ExitConfirmTitle"),
            body)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            if (tempOnRamDisk)
            {
                TempDirResetService.Reset();
            }
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void ExecuteFormatDisk(DiskViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        var confirm = new ConfirmDialog(
            Loc.Get("Msg.FormatDiskConfirmTitle"),
            Loc.Format("Msg.FormatDiskConfirmBody", vm.MountPoint, vm.VolumeLabel))
        {
            Owner = Application.Current.MainWindow
        };

        if (confirm.ShowDialog() != true || !IsStillMounted(vm))
        {
            return;
        }

        _logger.LogInformation("Format disk confirmed for {MountPoint}.", vm.MountPoint);

        // Format takes the disk's save lock, which a running auto-save can hold for a long time.
        var formatted = await Task.Run(vm.Disk.Format);

        // The disk can be unmounted by a CLI command or the tray while the format runs.
        if (!IsStillMounted(vm))
        {
            return;
        }

        if (!formatted)
        {
            _logger.LogWarning("Format disk failed for {MountPoint}: disk is read-only.", vm.MountPoint);
            ShowWarning(Loc.Get("Msg.FormatDiskReadOnly"));
            return;
        }

        vm.Refresh();
        StatusText = Loc.Format("Status.FormatDisk", vm.MountPoint);
        _logger.LogInformation("Format disk completed for {MountPoint}.", vm.MountPoint);
        ShowInfo(Loc.Format("Msg.FormatDiskSuccess", vm.MountPoint));
    }

    private async void ExecuteImportArchive()
    {
        if (RejectIfBusy())
        {
            return;
        }

        var openDialog = new OpenFileDialog
        {
            Title = Loc.Get("ImportArchiveDlg.Title"),
            Filter = Loc.Get("ArchiveDlg.Filter"),
            CheckFileExists = true,
        };

        if (openDialog.ShowDialog() != true)
        {
            return;
        }

        await ImportArchiveAsync(openDialog.FileName);
    }

    private async void ExecuteImportDisk()
    {
        if (RejectIfBusy())
        {
            return;
        }

        var openDialog = new OpenFileDialog
        {
            Title = Loc.Get("ImportDlg.Title"),
            Filter = Loc.Get("SaveDlg.Filter"),
            CheckFileExists = true,
        };

        if (openDialog.ShowDialog() != true)
        {
            return;
        }

        await ImportDiskAsync(openDialog.FileName);
    }

    /// <summary>
    /// Imports an archive file (<c>.zip</c> etc.) at <paramref name="archivePath"/> as a new
    /// read-only disk, prompting for mount options via <see cref="CreateDiskDialog"/>. Shared by
    /// <see cref="ExecuteImportArchive"/> (file picker) and drag-and-drop (see
    /// <see cref="ImportDroppedFileAsync"/>).
    /// </summary>
    private async Task ImportArchiveAsync(string archivePath)
    {
        var otherDisks = GetOtherDiskOptions(excluding: null);
        if (IsPathInUse(otherDisks, archivePath, d => d.SourceArchivePath))
        {
            ShowWarning(Loc.Get("Val.ArchivePathInUse"));
            return;
        }

        ulong totalBytes;
        string suggestedLabel;
        try
        {
            ArchiveNodeMapBuilder.PeekArchive(archivePath, out totalBytes, out suggestedLabel);
        }
        catch (InvalidDataException)
        {
            ShowWarning(Loc.Get("Val.ImportInvalidArchive"));
            return;
        }

        var dialog = CreateDiskDialog.ForArchiveImport(archivePath, totalBytes, suggestedLabel, otherDisks);
        dialog.Owner = Application.Current.MainWindow;

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _logger.LogInformation("Import archive requested: {ArchivePath} -> {MountPoint}.", archivePath, dialog.Result!.MountPoint);
        using var cts = new CancellationTokenSource();
        if (!TryStartBusyOverlay(Loc.Get("Busy.ImportingArchive"), indeterminate: totalBytes == 0, totalBytes: totalBytes > 0 ? totalBytes : null, cancellationSource: cts))
        {
            return;
        }

        try
        {
            var progress = new Progress<double>(BusyOverlay.Report);
            await MountAndAddAsync(dialog.Result!, progress: progress, cancellationToken: cts.Token);
        }
        finally
        {
            BusyOverlay.Stop();
        }
    }

    /// <summary>
    /// Imports a <c>.mdr</c> disk image at <paramref name="imagePath"/>, prompting for mount
    /// options via <see cref="CreateDiskDialog"/>. Shared by <see cref="ExecuteImportDisk"/>
    /// (file picker) and drag-and-drop (see <see cref="ImportDroppedFileAsync"/>).
    /// </summary>
    private async Task ImportDiskAsync(string imagePath)
    {
        var otherDisks = GetOtherDiskOptions(excluding: null);
        if (IsPathInUse(otherDisks, imagePath, d => d.PersistImagePath))
        {
            ShowWarning(Loc.Get("Val.ImagePathInUse"));
            return;
        }

        ulong capacityBytes;
        string volumeLabel;
        try
        {
            DiskImageSerializer.PeekHeader(imagePath, out capacityBytes, out volumeLabel, out _);
        }
        catch (InvalidDataException)
        {
            ShowWarning(Loc.Get("Val.ImportInvalidImage"));
            return;
        }

        var dialog = new CreateDiskDialog(imagePath, capacityBytes, volumeLabel, otherDisks)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _logger.LogInformation("Import disk image requested: {ImagePath} -> {MountPoint}.", imagePath, dialog.Result!.MountPoint);

        var fileSizeBytes = (ulong)new FileInfo(imagePath).Length;
        using var cts = new CancellationTokenSource();
        if (!TryStartBusyOverlay(Loc.Get("Busy.ImportingImage"), totalBytes: fileSizeBytes, cancellationSource: cts))
        {
            return;
        }

        try
        {
            var progress = new Progress<double>(BusyOverlay.Report);
            await MountAndAddAsync(dialog.Result!, progress: progress, cancellationToken: cts.Token);
        }
        finally
        {
            BusyOverlay.Stop();
        }
    }

    /// <summary>
    /// Imports a file dropped onto the main window: <c>.mdr</c> goes through
    /// <see cref="ImportDiskAsync"/>, anything else through <see cref="ImportArchiveAsync"/> (the
    /// archive importer validates the format itself and reports <c>Val.ImportInvalidArchive</c>
    /// if it isn't one).
    /// </summary>
    /// <param name="path">Absolute path of the dropped file.</param>
    public Task ImportDroppedFileAsync(string path) =>
        Path.GetExtension(path).Equals(".mdr", StringComparison.OrdinalIgnoreCase)
            ? ImportDiskAsync(path)
            : ImportArchiveAsync(path);

    private async void ExecuteResetTempDirs()
    {
        var confirm = new ConfirmDialog(
            Loc.Get("Msg.ResetTempConfirmTitle"),
            Loc.Get("Msg.ResetTempConfirmBody"))
        {
            Owner = Application.Current.MainWindow
        };

        if (confirm.ShowDialog() != true)
        {
            return;
        }

        _logger.LogInformation("Reset TEMP dirs confirmed.");
        var success = await Task.Run(TempDirResetService.Reset);

        if (success)
        {
            _logger.LogInformation("Reset TEMP dirs succeeded.");
            ShowInfo(Loc.Get("Msg.ResetTempSuccess"));
        }
        else
        {
            _logger.LogWarning("Reset TEMP dirs failed.");
            ShowError(Loc.Get("Msg.ResetTempFailed"));
        }
    }

    private async void ExecuteSaveImage(DiskViewModel? vm)
    {
        if (vm == null || RejectIfBusy())
        {
            return;
        }

        if (vm.Disk.Options.PersistImagePath == null)
        {
            var dlg = new SaveFileDialog
            {
                Title = Loc.Get("SaveDlg.Title"),
                Filter = Loc.Get("SaveDlg.Filter"),
                DefaultExt = ".mdr",
                OverwritePrompt = true,
            };

            if (dlg.ShowDialog() != true || !IsStillMounted(vm))
            {
                return;
            }

            var pathError = CreateDiskOptionsBuilder.ValidateImagePath(dlg.FileName, vm.Disk.Options.MountPoint, GetOtherDiskOptions(excluding: vm));
            if (pathError != CreateDiskValidationError.None)
            {
                _logger.LogWarning("Save image path rejected for {MountPoint}: {ImagePath} ({Error}).", vm.MountPoint, dlg.FileName, pathError);
                ShowWarning(pathError switch
                {
                    CreateDiskValidationError.ImagePathIsSnapshot => Loc.Get("Val.ImagePathIsSnapshot"),
                    CreateDiskValidationError.ImagePathInUse => Loc.Get("Val.ImagePathInUse"),
                    CreateDiskValidationError.ImagePathOnRamDisk => Loc.Get("Val.ImagePathOnRamDisk"),
                    _ => Loc.Get("Val.BadImagePath"),
                });
                return;
            }

            var newOptions = vm.Disk.Options with { PersistImagePath = dlg.FileName };
            string? applyError = null;

            // Takes the disk's save lock, which a running auto-save can hold for a long time.
            var applied = await Task.Run(() => vm.Disk.TryApplyOptions(newOptions, out applyError));
            if (!IsStillMounted(vm))
            {
                return;
            }

            if (!applied)
            {
                _logger.LogWarning("Save image path could not be applied for {MountPoint}: {Error}", vm.MountPoint, applyError);
                ShowWarning(applyError);
                return;
            }

            SaveSettings();
        }

        _logger.LogInformation("Save image requested for {MountPoint}.", vm.MountPoint);
        await SaveImageWithSnapshotAsync(vm, Loc.Get("Busy.SavingImage"), "Save image");
    }

    private void ExecuteSettings()
    {
        var config = _settingsStore.Load();
        var dialog = new SettingsDialog(config) { Owner = Application.Current.MainWindow };

        if (dialog.ShowDialog() == true)
        {
            // The dialog is modal but the dispatcher keeps running under it, so CLI commands, the
            // tray menu and update checks may have saved settings since `config` was read.
            _settingsStore.Update(latest => SettingsDialog.ApplyEdits(latest, dialog.Result!));
            _logger.LogInformation("Settings saved.");
        }
    }

    private async void ExecuteToggleTempDir(DiskViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        if (vm.IsCurrentTempDir)
        {
            _logger.LogInformation("TEMP dir reset requested (was pointing at {MountPoint}).", vm.MountPoint);
            var success = await Task.Run(TempDirResetService.Reset);

            if (success)
            {
                ShowInfo(Loc.Get("Msg.ResetTempSuccess"));
                vm.Refresh();
            }
            else
            {
                _logger.LogWarning("TEMP dir reset failed.");
                ShowError(Loc.Get("Msg.ResetTempFailed"));
            }
        }
        else
        {
            if (!_settingsStore.Load().TempDirCompatWarningShown)
            {
                var warn = new ConfirmDialog(
                    Loc.Get("Msg.SetTempDirWarningTitle"),
                    Loc.Get("Msg.SetTempDirWarningBody"))
                {
                    Owner = Application.Current.MainWindow
                };
                if (warn.ShowDialog() != true || !IsStillMounted(vm))
                {
                    return;
                }

                _settingsStore.Update(current => current with { TempDirCompatWarningShown = true });
            }

            var tempPath = Path.Combine(vm.MountPoint, "Temp");
            _logger.LogInformation("Set TEMP dir requested: {TempPath}.", tempPath);
            var success = await Task.Run(() => TempDirResetService.Set(tempPath));

            if (success)
            {
                ShowInfo(Loc.Format("Msg.SetTempDirSuccess", tempPath));
                StatusText = Loc.Format("Status.TempDirSet", tempPath);
                vm.Refresh();
            }
            else
            {
                _logger.LogWarning("Set TEMP dir failed: {TempPath}.", tempPath);
                ShowError(Loc.Get("Msg.SetTempDirFailed"));
            }
        }
    }

    private async void ExecuteUnmount(DiskViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        var body = Loc.Format("Msg.UnmountConfirmBody", vm.MountPoint, vm.VolumeLabel);
        if (vm.IsCurrentTempDir)
        {
            body += "\n\n" + Loc.Get("Msg.TempDirWillBeReset");
        }

        var confirm = new ConfirmDialog(Loc.Get("Msg.UnmountConfirmTitle"), body)
        {
            Owner = Application.Current.MainWindow
        };

        if (vm.HasImagePath)
        {
            confirm.ShowOption(Loc.Get("Msg.DeleteImageOption"));
        }

        if (confirm.ShowDialog() != true || !IsStillMounted(vm))
        {
            return;
        }

        var deleteImage = confirm.IsOptionChecked;
        var persistImagePath = vm.PersistImagePath;
        var sourceArchivePath = vm.Disk.Options.SourceArchivePath;

        _logger.LogInformation("Unmount confirmed for {MountPoint} (deleteImage: {DeleteImage}).", vm.MountPoint, deleteImage);

        if (vm.IsCurrentTempDir)
        {
            await Task.Run(TempDirResetService.Reset);
        }

        var mountPoint = vm.Disk.Options.MountPoint;
        var saveError = await UnmountDiskAsync(vm);

        await DeleteDiskImageIfRequestedAsync(deleteImage, persistImagePath, sourceArchivePath);

        SaveSettings();
        ShowUnmountResult(mountPoint, saveError);
        _logger.LogInformation("Unmount completed for {MountPoint}.", mountPoint);
    }

    private void ExecuteViewDiskContents(DiskViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        var dialog = new DiskContentDialog(vm)
        {
            Owner = Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }
}
