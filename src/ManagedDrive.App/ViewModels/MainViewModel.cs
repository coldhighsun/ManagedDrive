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
        ExitCommand = new RelayCommand(_ => ExecuteExit());
        UnmountCommand = new RelayCommand(
            p => ExecuteUnmount(p as DiskViewModel ?? SelectedDisk),
            p => p is DiskViewModel || SelectedDisk != null);
        SaveImageCommand = new RelayCommand(
            p => ExecuteSaveImage(p as DiskViewModel ?? SelectedDisk),
            p => p is DiskViewModel vm ? vm.Disk.Options.PersistImagePath != null
                                       : SelectedDisk?.Disk.Options.PersistImagePath != null);
        RefreshCommand = new RelayCommand(_ => RefreshAll());
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

        try
        {
            vm.Disk.SaveToImage();
            StatusText = Loc.Format("Status.ImageSaved", vm.MountPoint);
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

    private void ExecuteUnmount(DiskViewModel? vm)
    {
        if (vm == null)
        {
            return;
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