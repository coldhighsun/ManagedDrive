using System.Collections.ObjectModel;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// View model for <see cref="ManagedDrive.App.MainWindow"/>. Manages the collection of active
/// disks and exposes commands for the toolbar and context menu.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MountManager _mountManager;
    private readonly SettingsStore _settingsStore;
    private double _highUsageResetPercent;
    private double _highUsageWarnPercent;
    private bool _tempDirCompatWarningShown;

    /// <summary>
    /// Initializes a new <see cref="MainViewModel"/> using the supplied mount manager and settings store.
    /// </summary>
    /// <param name="mountManager">The application-wide mount manager.</param>
    /// <param name="settingsStore">The settings store used by the Settings dialog.</param>
    /// <param name="initialConfig">The configuration loaded at startup.</param>
    public MainViewModel(MountManager mountManager, SettingsStore settingsStore, AppConfiguration initialConfig)
    {
        _mountManager = mountManager;
        _settingsStore = settingsStore;
        _tempDirCompatWarningShown = initialConfig.TempDirCompatWarningShown;
        _highUsageWarnPercent = initialConfig.HighUsageWarnPercent;
        _highUsageResetPercent = initialConfig.HighUsageResetPercent;

        StatusText = Loc.Get("Status.Ready");

        CreateDiskCommand = new(_ => ExecuteCreateDisk());
        ImportDiskCommand = new(_ => ExecuteImportDisk());
        EditDiskCommand = new(
            p => ExecuteEditDisk(p as DiskViewModel ?? SelectedDisk),
            p => p is DiskViewModel || SelectedDisk != null);
        ExitCommand = new(_ => ExecuteExit());
        UnmountCommand = new(
            p => ExecuteUnmount(p as DiskViewModel ?? SelectedDisk),
            p => p is DiskViewModel || SelectedDisk != null);
        SaveImageCommand = new(
            p => ExecuteSaveImage(p as DiskViewModel ?? SelectedDisk),
            p => p is DiskViewModel || SelectedDisk != null);
        FormatDiskCommand = new(
            p => ExecuteFormatDisk(p as DiskViewModel ?? SelectedDisk),
            p =>
            {
                var vm = p as DiskViewModel ?? SelectedDisk;
                return vm is { Disk.Options.ReadOnly: false };
            });
        CloneDiskCommand = new(
            p => ExecuteCloneDisk(p as DiskViewModel ?? SelectedDisk),
            p => p is DiskViewModel || SelectedDisk != null);
        RestoreSnapshotCommand = new(
            p => ExecuteRestoreSnapshot(p as DiskViewModel ?? SelectedDisk),
            p =>
            {
                var vm = p as DiskViewModel ?? SelectedDisk;
                return vm is { IsReadOnly: false, HasImagePath: true };
            });
        RefreshCommand = new(_ => RefreshAll());
        ResetTempDirsCommand = new(_ => ExecuteResetTempDirs());
        ToggleTempDirCommand = new(
            p => ExecuteToggleTempDir(p as DiskViewModel ?? SelectedDisk),
            p =>
            {
                var vm = p as DiskViewModel ?? SelectedDisk;
                return vm is { Disk.Options.ReadOnly: false };
            });
        SettingsCommand = new(_ => ExecuteSettings());
        AboutCommand = new(_ => ExecuteAbout());
    }

    public event EventHandler? ExitRequested;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the command that opens the About dialog.
    /// </summary>
    public RelayCommand AboutCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that opens the "Clone Disk" dialog for the selected disk: copy its
    /// contents onto another mounted disk, or export them to a new image file.
    /// </summary>
    public RelayCommand CloneDiskCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that opens the "Create Disk" dialog.
    /// </summary>
    public RelayCommand CreateDiskCommand
    {
        get;
    }

    /// <summary>
    /// Gets the observable list of active disk view models displayed in the main grid.
    /// </summary>
    public ObservableCollection<DiskViewModel> Disks { get; } = [];

    /// <summary>
    /// Gets the command that opens the "Edit Disk" dialog for the selected disk.
    /// </summary>
    public RelayCommand EditDiskCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that exits the application.
    /// </summary>
    public RelayCommand ExitCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that formats (clears all content from) the selected disk.
    /// </summary>
    public RelayCommand FormatDiskCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that opens the "Import Disk" flow: pick an existing .mdr image file and
    /// mount it, pre-filling capacity/volume label from the image itself.
    /// </summary>
    public RelayCommand ImportDiskCommand
    {
        get;
    }

    /// <summary>
    /// Gets whether the application is currently shutting down (saving disk images).
    /// </summary>
    public bool IsExiting
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(IsExiting));
        }
    }

    /// <summary>
    /// Gets the command that refreshes usage statistics.
    /// </summary>
    public RelayCommand RefreshCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that resets Windows TEMP and TMP directories to their OS defaults.
    /// </summary>
    public RelayCommand ResetTempDirsCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that opens the "Restore Snapshot" dialog for the selected disk: pick a
    /// previously saved timestamped snapshot and replace the disk's live contents with it.
    /// </summary>
    public RelayCommand RestoreSnapshotCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that saves the selected disk's image to file.
    /// </summary>
    public RelayCommand SaveImageCommand
    {
        get;
    }

    /// <summary>
    /// Gets or sets the currently selected disk in the list.
    /// </summary>
    public DiskViewModel? SelectedDisk
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(SelectedDisk));
        }
    }

    /// <summary>
    /// Gets the command that opens the Settings dialog.
    /// </summary>
    public RelayCommand SettingsCommand
    {
        get;
    }

    /// <summary>
    /// Gets the status bar text.
    /// </summary>
    public string StatusText
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>
    /// Gets the command that toggles the user's TEMP/TMP between the selected disk's
    /// Temp folder and the Windows default, depending on the current state.
    /// </summary>
    public RelayCommand ToggleTempDirCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that unmounts the selected disk.
    /// </summary>
    public RelayCommand UnmountCommand
    {
        get;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var vm in Disks)
        {
            vm.Dispose();
        }
    }

    /// <summary>
    /// Returns a <see cref="DiskProfile"/> snapshot for every currently active disk.
    /// </summary>
    /// <returns>
    /// A sequence of <see cref="DiskProfile"/> representing all mounted disks.
    /// </returns>
    public IEnumerable<DiskProfile> GetProfiles()
    {
        return Disks.Select(vm => new DiskProfile
        {
            MountPoint = vm.Disk.Options.MountPoint,
            VolumeLabel = vm.Disk.Options.VolumeLabel,
            CapacityBytes = vm.Disk.Options.CapacityBytes,
            ReadOnly = vm.Disk.Options.ReadOnly,
            AutoMount = vm.Disk.Options.AutoMount,
            PersistImagePath = vm.Disk.Options.PersistImagePath,
            AutoSaveIntervalMinutes = vm.Disk.Options.AutoSaveIntervalMinutes,
            CompressionLevel = vm.Disk.Options.CompressionLevel,
            MaxSnapshotCount = vm.Disk.Options.MaxSnapshotCount,
            MaxSnapshotSizeBytes = vm.Disk.Options.MaxSnapshotSizeBytes,
        });
    }

    /// <summary>
    /// Mounts a disk from a saved <see cref="DiskProfile"/> and adds it to the list.
    /// Errors are surfaced via <see cref="StatusText"/>.
    /// </summary>
    /// <param name="profile">The profile to mount.</param>
    public async void MountFromProfile(DiskProfile profile)
    {
        try
        {
            var options = ProfileToOptions(profile);
            var disk = await Task.Run(() => _mountManager.Mount(options));
            AddDiskSorted(new(disk)
            {
                HighUsageThreshold = _highUsageWarnPercent,
                HighUsageResetThreshold = _highUsageResetPercent,
            });
            StatusText = Loc.Format("Status.Mounted", disk.MountPoint, profile.VolumeLabel);
        }
        catch (Exception ex)
        {
            StatusText = Loc.Format("Status.AutoMountFailed", profile.MountPoint, ex.Message);

            var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
            if (!string.IsNullOrEmpty(userTemp))
            {
                var expanded = Environment.ExpandEnvironmentVariables(userTemp);
                if (expanded.StartsWith(profile.MountPoint, StringComparison.OrdinalIgnoreCase))
                {
                    TempDirResetService.Reset();
                }
            }
        }
    }

    internal void SaveSettings()
    {
        var current = _settingsStore.Load();
        _settingsStore.Save(new()
        {
            RunAtStartup = StartupManager.IsEnabled,
            StartMinimized = current.StartMinimized,
            Language = LanguageManager.Instance.SavedLanguage,
            Theme = ThemeManager.Instance.SavedTheme,
            Disks = GetProfiles().ToList(),
            TempDirCompatWarningShown = _tempDirCompatWarningShown,
            HighUsageWarnPercent = _highUsageWarnPercent,
            HighUsageResetPercent = _highUsageResetPercent,
        });
    }

    private static DiskOptions ProfileToOptions(DiskProfile p) => new()
    {
        MountPoint = p.MountPoint,
        VolumeLabel = p.VolumeLabel,
        CapacityBytes = p.CapacityBytes,
        ReadOnly = p.ReadOnly,
        AutoMount = p.AutoMount,
        PersistImagePath = p.PersistImagePath,
        AutoSaveIntervalMinutes = p.AutoSaveIntervalMinutes,
        CompressionLevel = p.CompressionLevel,
        MaxSnapshotCount = p.MaxSnapshotCount,
        MaxSnapshotSizeBytes = p.MaxSnapshotSizeBytes,
    };

    private void AddDiskSorted(DiskViewModel vm)
    {
        var i = 0;
        while (i < Disks.Count &&
               string.Compare(Disks[i].MountPoint, vm.MountPoint, StringComparison.OrdinalIgnoreCase) < 0)
        {
            i++;
        }
        Disks.Insert(i, vm);
    }

    private void ExecuteAbout()
    {
        var dialog = new AboutDialog();
        if (Application.Current.MainWindow is { IsLoaded: true } mainWindow)
        {
            dialog.Owner = mainWindow;
        }

        dialog.ShowDialog();
    }

    private async void ExecuteCloneDisk(DiskViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        var targets = Disks.Where(d => d != vm && !d.IsReadOnly).ToList();

        // Include the source disk's own options (excluding: null) so exporting to a path that
        // the source itself is already persisting to is also rejected — that file may be
        // concurrently written by the source's auto-save timer.
        var dialog = new CloneDiskDialog(vm, targets, GetOtherDiskOptions(excluding: null))
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true)
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

            if (confirm.ShowDialog() != true)
            {
                return;
            }

            if (!target.Disk.TryCloneFrom(vm.Disk, out var error))
            {
                MessageBox.Show(
                    error,
                    "ManagedDrive",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            target.Refresh();
            StatusText = Loc.Format("Status.DiskCloned", vm.MountPoint, target.MountPoint);
        }
        else if (dialog.ExportPath is { } exportPath)
        {
            try
            {
                await Task.Run(() => vm.Disk.ExportToImage(exportPath, dialog.ExportCompressionLevel));
                StatusText = Loc.Format("Status.DiskExported", vm.MountPoint, exportPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Loc.Format("Msg.SaveImageFailed", ex.Message),
                    "ManagedDrive",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private async void ExecuteCreateDisk()
    {
        var dialog = new CreateDiskDialog(GetOtherDiskOptions(excluding: null))
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await MountAndAddAsync(dialog.Result!);
    }

    private async void ExecuteEditDisk(DiskViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        var dialog = new CreateDiskDialog(vm.Disk.Options, GetOtherDiskOptions(excluding: vm))
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var newOptions = dialog.Result!;
        var old = vm.Disk.Options;
        var needsRemount = newOptions.MountPoint != old.MountPoint || newOptions.ReadOnly != old.ReadOnly;

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

            if (confirm.ShowDialog() != true)
            {
                return;
            }

            if (vm.IsCurrentTempDir)
            {
                await Task.Run(TempDirResetService.Reset);
            }

            var oldMountPoint = old.MountPoint;
            vm.Dispose();
            Disks.Remove(vm);
            await Task.Run(() => _mountManager.Unmount(oldMountPoint));

            try
            {
                var disk = await Task.Run(() => _mountManager.Mount(newOptions));
                AddDiskSorted(new(disk)
                {
                    HighUsageThreshold = _highUsageWarnPercent,
                    HighUsageResetThreshold = _highUsageResetPercent,
                });
                SaveSettings();
                StatusText = Loc.Format("Status.MountedWithCapacity", disk.MountPoint, newOptions.VolumeLabel, newOptions.CapacityBytes / (1024 * 1024));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Loc.Format("Msg.MountFailed", ex.Message),
                    "ManagedDrive",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                StatusText = Loc.Get("Status.MountFailed");
            }
        }
        else
        {
            if (!vm.Disk.TryApplyOptions(newOptions, out var error))
            {
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
        }
    }

    private void ExecuteExit()
    {
        if (Disks.Count == 0)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        var expandedTemp = string.IsNullOrEmpty(userTemp) ? null : Environment.ExpandEnvironmentVariables(userTemp);
        var tempOnRamDisk = expandedTemp != null &&
            Disks.Any(d => expandedTemp.StartsWith(d.MountPoint, StringComparison.OrdinalIgnoreCase));

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

    private void ExecuteFormatDisk(DiskViewModel? vm)
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

        if (confirm.ShowDialog() != true)
        {
            return;
        }

        if (!vm.Disk.Format())
        {
            MessageBox.Show(
                Loc.Get("Msg.FormatDiskReadOnly"),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        vm.Refresh();
        StatusText = Loc.Format("Status.FormatDisk", vm.MountPoint);
        MessageBox.Show(
            Loc.Format("Msg.FormatDiskSuccess", vm.MountPoint),
            "ManagedDrive",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void ExecuteImportDisk()
    {
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

        var otherDisks = GetOtherDiskOptions(excluding: null);
        if (otherDisks.Any(d => d.PersistImagePath != null &&
            string.Equals(d.PersistImagePath, openDialog.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                Loc.Get("Val.ImagePathInUse"),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ulong capacityBytes;
        string volumeLabel;
        try
        {
            DiskImageSerializer.PeekHeader(openDialog.FileName, out capacityBytes, out volumeLabel);
        }
        catch (InvalidDataException)
        {
            MessageBox.Show(
                Loc.Get("Val.ImportInvalidImage"),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var dialog = new CreateDiskDialog(openDialog.FileName, capacityBytes, volumeLabel, otherDisks)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await MountAndAddAsync(dialog.Result!);
    }

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

        var success = await Task.Run(TempDirResetService.Reset);

        if (success)
        {
            MessageBox.Show(
                Loc.Get("Msg.ResetTempSuccess"),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                Loc.Get("Msg.ResetTempFailed"),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void ExecuteRestoreSnapshot(DiskViewModel? vm)
    {
        if (vm == null || vm.Disk.Options.PersistImagePath is not { } imagePath)
        {
            return;
        }

        var snapshots = await Task.Run(() => SnapshotManager.ListSnapshots(imagePath));
        if (snapshots.Count == 0)
        {
            MessageBox.Show(
                Loc.Get("Msg.NoSnapshotsAvailable"),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new RestoreSnapshotDialog(vm, snapshots)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true || dialog.SelectedSnapshotPath is not { } selectedPath)
        {
            return;
        }

        var confirm = new ConfirmDialog(
            Loc.Get("Msg.RestoreSnapshotConfirmTitle"),
            Loc.Format("Msg.RestoreSnapshotConfirmBody", vm.MountPoint, vm.VolumeLabel, dialog.SelectedSnapshotLabel))
        {
            Owner = Application.Current.MainWindow
        };

        if (confirm.ShowDialog() != true)
        {
            return;
        }

        string? error = null;
        var success = await Task.Run(() => vm.Disk.TryRestoreFromSnapshot(selectedPath, out error));

        if (!success)
        {
            MessageBox.Show(
                error,
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        vm.Refresh();
        StatusText = Loc.Format("Status.SnapshotRestored", vm.MountPoint);
    }

    private async void ExecuteSaveImage(DiskViewModel? vm)
    {
        if (vm == null)
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

            if (dlg.ShowDialog() != true)
            {
                return;
            }

            vm.Disk.TryApplyOptions(vm.Disk.Options with
            {
                PersistImagePath = dlg.FileName
            }, out _);
            SaveSettings();
        }

        vm.IsSaving = true;
        try
        {
            await Task.Run(() => vm.Disk.SaveToImage());
            StatusText = Loc.Format("Status.ImageSaved", vm.MountPoint);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.Format("Msg.SaveImageFailed", ex.Message),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            vm.IsSaving = false;
        }
    }

    private void ExecuteSettings()
    {
        var config = _settingsStore.Load();
        var dialog = new SettingsDialog(config) { Owner = Application.Current.MainWindow };

        if (dialog.ShowDialog() == true)
        {
            var updated = dialog.Result!;
            updated.Disks = config.Disks;
            _settingsStore.Save(updated);

            _highUsageWarnPercent = updated.HighUsageWarnPercent;
            _highUsageResetPercent = updated.HighUsageResetPercent;
            foreach (var vm in Disks)
            {
                vm.HighUsageThreshold = _highUsageWarnPercent;
                vm.HighUsageResetThreshold = _highUsageResetPercent;
            }
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
            var success = await Task.Run(TempDirResetService.Reset);

            if (success)
            {
                MessageBox.Show(
                    Loc.Get("Msg.ResetTempSuccess"),
                    "ManagedDrive",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                vm.Refresh();
            }
            else
            {
                MessageBox.Show(
                    Loc.Get("Msg.ResetTempFailed"),
                    "ManagedDrive",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        else
        {
            if (!_tempDirCompatWarningShown)
            {
                var warn = new ConfirmDialog(
                    Loc.Get("Msg.SetTempDirWarningTitle"),
                    Loc.Get("Msg.SetTempDirWarningBody"))
                {
                    Owner = Application.Current.MainWindow
                };
                if (warn.ShowDialog() != true)
                {
                    return;
                }

                _tempDirCompatWarningShown = true;
                SaveSettings();
            }

            var tempPath = Path.Combine(vm.MountPoint, "Temp");
            var success = await Task.Run(() => TempDirResetService.Set(tempPath));

            if (success)
            {
                MessageBox.Show(
                    Loc.Format("Msg.SetTempDirSuccess", tempPath),
                    "ManagedDrive",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                StatusText = Loc.Format("Status.TempDirSet", tempPath);
                vm.Refresh();
            }
            else
            {
                MessageBox.Show(
                    Loc.Get("Msg.SetTempDirFailed"),
                    "ManagedDrive",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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

        if (confirm.ShowDialog() != true)
        {
            return;
        }

        var deleteImage = confirm.IsOptionChecked;
        var imagePath = vm.PersistImagePath;

        if (vm.IsCurrentTempDir)
        {
            await Task.Run(TempDirResetService.Reset);
        }

        var mountPoint = vm.Disk.Options.MountPoint;
        vm.Dispose();
        Disks.Remove(vm);
        await Task.Run(() => _mountManager.Unmount(mountPoint));

        if (deleteImage && imagePath != null)
        {
            try { File.Delete(imagePath); } catch { }
            await Task.Run(() => SnapshotManager.DeleteAllSnapshots(imagePath));
        }

        SaveSettings();
        StatusText = Loc.Format("Status.Unmounted", mountPoint);
    }

    /// <summary>
    /// Returns the <see cref="DiskOptions"/> of every currently active disk except
    /// <paramref name="excluding"/>. Used to validate that a new or edited disk's image file
    /// path does not collide with another disk's mount point or image file.
    /// </summary>
    private IReadOnlyList<DiskOptions> GetOtherDiskOptions(DiskViewModel? excluding) =>
        Disks.Where(d => d != excluding).Select(d => d.Disk.Options).ToList();

    private async Task MountAndAddAsync(DiskOptions options)
    {
        try
        {
            var disk = await Task.Run(() => _mountManager.Mount(options));
            AddDiskSorted(new(disk)
            {
                HighUsageThreshold = _highUsageWarnPercent,
                HighUsageResetThreshold = _highUsageResetPercent,
            });
            SaveSettings();
            StatusText = Loc.Format("Status.MountedWithCapacity", disk.MountPoint, options.VolumeLabel, options.CapacityBytes / (1024 * 1024));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.Format("Msg.MountFailed", ex.Message),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusText = Loc.Get("Status.MountFailed");
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    private void RefreshAll()
    {
        foreach (var vm in Disks)
        {
            vm.Refresh();
        }
    }
}