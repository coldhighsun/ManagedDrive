using ManagedDrive.App.Infrastructure;
using ManagedDrive.App.Localization;
using ManagedDrive.App.Models;
using ManagedDrive.App.Services;
using ManagedDrive.App.Views;
using ManagedDrive.Core;
using Serilog;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// View model for <see cref="ManagedDrive.App.MainWindow"/>. Manages the collection of active
/// disks and exposes commands for the toolbar and context menu.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MountManager _mountManager;
    private readonly SettingsStore _settingsStore;

    /// <summary>
    /// Initializes a new <see cref="MainViewModel"/> using the supplied mount manager and settings store.
    /// </summary>
    /// <param name="mountManager">The application-wide mount manager.</param>
    /// <param name="settingsStore">The settings store used by the Settings dialog.</param>
    public MainViewModel(MountManager mountManager, SettingsStore settingsStore)
    {
        _mountManager = mountManager;
        _settingsStore = settingsStore;

        StatusText = Loc.Get("Status.Ready");

        CreateDiskCommand = new RelayCommand(_ => ExecuteCreateDisk());
        EditDiskCommand = new RelayCommand(
            p => ExecuteEditDisk(p as DiskViewModel ?? SelectedDisk),
            p => p is DiskViewModel || SelectedDisk != null);
        ExitCommand = new RelayCommand(_ => ExecuteExit());
        UnmountCommand = new RelayCommand(
            p => ExecuteUnmount(p as DiskViewModel ?? SelectedDisk),
            p => p is DiskViewModel || SelectedDisk != null);
        SaveImageCommand = new RelayCommand(
            p => ExecuteSaveImage(p as DiskViewModel ?? SelectedDisk),
            p => p is DiskViewModel || SelectedDisk != null);
        RefreshCommand = new RelayCommand(_ => RefreshAll());
        ResetTempDirsCommand = new RelayCommand(_ => ExecuteResetTempDirs());
        ToggleTempDirCommand = new RelayCommand(
            p => ExecuteToggleTempDir(p as DiskViewModel ?? SelectedDisk),
            p => p is DiskViewModel || SelectedDisk != null);
        SettingsCommand = new RelayCommand(_ => ExecuteSettings());
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the command that opens the "Create Disk" dialog.
    /// </summary>
    public RelayCommand CreateDiskCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that opens the "Edit Disk" dialog for the selected disk.
    /// </summary>
    public RelayCommand EditDiskCommand
    {
        get;
    }

    /// <summary>
    /// Gets the observable list of active disk view models displayed in the main grid.
    /// </summary>
    public ObservableCollection<DiskViewModel> Disks { get; } = [];

    /// <summary>
    /// Gets the command that exits the application.
    /// </summary>
    public RelayCommand ExitCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that refreshes usage statistics.
    /// </summary>
    public RelayCommand RefreshCommand
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
    /// Gets the command that resets Windows TEMP and TMP directories to their OS defaults.
    /// </summary>
    public RelayCommand ResetTempDirsCommand
    {
        get;
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
            AddDiskSorted(new DiskViewModel(disk));
            StatusText = Loc.Format("Status.Mounted", disk.MountPoint, profile.VolumeLabel);
            Log.Information("Auto-mounted {MountPoint} ({Label}).", disk.MountPoint, profile.VolumeLabel);
        }
        catch (Exception ex)
        {
            StatusText = Loc.Format("Status.AutoMountFailed", profile.MountPoint, ex.Message);
            Log.Error(ex, "Auto-mount {MountPoint} failed.", profile.MountPoint);

            var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
            if (!string.IsNullOrEmpty(userTemp))
            {
                var expanded = Environment.ExpandEnvironmentVariables(userTemp);
                if (expanded.StartsWith(profile.MountPoint, StringComparison.OrdinalIgnoreCase))
                {
                    TempDirResetService.Reset();
                    Log.Warning("Auto-reset TEMP to default because auto-mount of {MountPoint} failed.", profile.MountPoint);
                }
            }
        }
    }

    private static DiskOptions ProfileToOptions(DiskProfile p) => new()
    {
        MountPoint = p.MountPoint,
        VolumeLabel = p.VolumeLabel,
        CapacityBytes = p.CapacityBytes,
        ReadOnly = p.ReadOnly,
        AutoMount = p.AutoMount,
        PersistImagePath = p.PersistImagePath,
    };

    private void AddDiskSorted(DiskViewModel vm)
    {
        int i = 0;
        while (i < Disks.Count &&
               string.Compare(Disks[i].MountPoint, vm.MountPoint, StringComparison.OrdinalIgnoreCase) < 0)
        {
            i++;
        }
        Disks.Insert(i, vm);
    }

    private async void ExecuteCreateDisk()
    {
        var dialog = new CreateDiskDialog { Owner = Application.Current.MainWindow };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var options = dialog.Result!;
            var disk = await Task.Run(() => _mountManager.Mount(options));
            AddDiskSorted(new DiskViewModel(disk));
            SaveSettings();
            StatusText = Loc.Format("Status.MountedWithCapacity", disk.MountPoint, options.VolumeLabel, options.CapacityBytes / (1024 * 1024));
            Log.Information(
                "Mounted {MountPoint} ({Label}, {CapacityMb} MB).",
                disk.MountPoint,
                options.VolumeLabel,
                options.CapacityBytes / (1024 * 1024));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.Format("Msg.MountFailed", ex.Message),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusText = Loc.Get("Status.MountFailed");
            Log.Error(ex, "Failed to mount disk.");
        }
    }

    private async void ExecuteEditDisk(DiskViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        var dialog = new CreateDiskDialog(vm.Disk.Options) { Owner = Application.Current.MainWindow };

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
                Log.Information("Auto-reset temp directory before editing {MountPoint}.", vm.MountPoint);
            }

            var oldMountPoint = old.MountPoint;
            vm.Dispose();
            Disks.Remove(vm);
            _mountManager.Unmount(oldMountPoint);

            try
            {
                var disk = await Task.Run(() => _mountManager.Mount(newOptions));
                AddDiskSorted(new DiskViewModel(disk));
                SaveSettings();
                StatusText = Loc.Format("Status.MountedWithCapacity", disk.MountPoint, newOptions.VolumeLabel, newOptions.CapacityBytes / (1024 * 1024));
                Log.Information(
                    "Edited disk: remounted {MountPoint} ({Label}, {CapacityMb} MB).",
                    disk.MountPoint,
                    newOptions.VolumeLabel,
                    newOptions.CapacityBytes / (1024 * 1024));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Loc.Format("Msg.MountFailed", ex.Message),
                    "ManagedDrive",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                StatusText = Loc.Get("Status.MountFailed");
                Log.Error(ex, "Failed to remount disk after edit.");
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
            Log.Information(
                "Edited disk: hot-updated {MountPoint} ({Label}, {CapacityMb} MB).",
                vm.MountPoint,
                newOptions.VolumeLabel,
                newOptions.CapacityBytes / (1024 * 1024));
        }
    }

    private void ExecuteExit()
    {
        if (Disks.Count == 0)
        {
            Application.Current.Shutdown();
            return;
        }

        var dialog = new ConfirmDialog(
            Loc.Get("Msg.ExitConfirmTitle"),
            Loc.Get("Msg.ExitConfirmBody"))
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            Application.Current.Shutdown();
        }
    }

    private void ExecuteSaveImage(DiskViewModel? vm)
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

            vm.Disk.TryApplyOptions(vm.Disk.Options with { PersistImagePath = dlg.FileName }, out _);
            SaveSettings();
        }

        try
        {
            vm.Disk.SaveToImage();
            StatusText = Loc.Format("Status.ImageSaved", vm.MountPoint);
            MessageBox.Show(
                Loc.Format("Msg.SaveImageSuccess", vm.Disk.Options.PersistImagePath),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Log.Information("Saved image for {MountPoint}.", vm.MountPoint);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.Format("Msg.SaveImageFailed", ex.Message),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Log.Error(ex, "Failed to save image for {MountPoint}.", vm.MountPoint);
        }
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
            Log.Information("User temp directories reset to defaults.");
        }
        else
        {
            MessageBox.Show(
                Loc.Get("Msg.ResetTempFailed"),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Log.Warning("Failed to reset user temp directories.");
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
                Log.Information("User temp directories reset to defaults from {MountPoint}.", vm.MountPoint);
            }
            else
            {
                MessageBox.Show(
                    Loc.Get("Msg.ResetTempFailed"),
                    "ManagedDrive",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Log.Warning("Failed to reset user temp directories.");
            }
        }
        else
        {
            var tempPath = System.IO.Path.Combine(vm.MountPoint, "Temp");
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
                Log.Information("User temp directory set to {TempPath}.", tempPath);
            }
            else
            {
                MessageBox.Show(
                    Loc.Get("Msg.SetTempDirFailed"),
                    "ManagedDrive",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Log.Warning("Failed to set user temp directory to {TempPath}.", tempPath);
            }
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

        if (confirm.ShowDialog() != true)
        {
            return;
        }

        if (vm.IsCurrentTempDir)
        {
            await Task.Run(TempDirResetService.Reset);
            Log.Information("Auto-reset temp directory before unmounting {MountPoint}.", vm.MountPoint);
        }

        var mountPoint = vm.Disk.Options.MountPoint;
        vm.Dispose();
        Disks.Remove(vm);
        _mountManager.Unmount(mountPoint);
        SaveSettings();
        StatusText = Loc.Format("Status.Unmounted", mountPoint);
        Log.Information("Unmounted {MountPoint}.", mountPoint);
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void RefreshAll()
    {
        foreach (var vm in Disks)
        {
            vm.Refresh();
        }
    }

    private void SaveSettings()
    {
        _settingsStore.Save(new AppConfiguration
        {
            RunAtStartup = StartupManager.IsEnabled,
            Language = LanguageManager.Instance.SavedLanguage,
            Disks = GetProfiles().ToList(),
        });
    }
}