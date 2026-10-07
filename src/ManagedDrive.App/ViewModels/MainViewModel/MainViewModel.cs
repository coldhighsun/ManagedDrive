using ManagedDrive.Cli.Core;
using System.Collections.ObjectModel;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// View model for <see cref="ManagedDrive.App.MainWindow"/>. Manages the collection of active
/// disks and exposes commands for the toolbar and context menu.
/// </summary>
public sealed partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan DiskActivityStatusDuration = TimeSpan.FromSeconds(2.5);

    private readonly DispatcherTimer _diskActivityStatusTimer;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _memoryRefreshTimer;
    private readonly MountManager _mountManager;
    private readonly SettingsStore _settingsStore;

    /// <summary>
    /// Disks that were removed from <see cref="Disks"/> but are still running their final save
    /// inside <see cref="MountManager.Unmount"/>. Only touched on the UI thread.
    /// </summary>
    private readonly List<(RamDisk Disk, Task Task)> _pendingUnmounts = [];

    /// <summary>
    /// Saved profiles that are not mounted right now: every profile loaded at startup until it
    /// mounts (see <see cref="RetainSavedProfiles"/>) — including auto-mount profiles still
    /// waiting their turn and profiles with auto-mount turned off, which are never mounted at
    /// startup — plus auto-mount profiles that failed to mount this session (image on an
    /// unplugged drive, cancelled password prompt, drive letter taken, ...).
    /// <see cref="SaveSettings"/> keeps writing them back so a save that happens before (or
    /// instead of) their mount doesn't permanently delete the disk's profile; a profile is
    /// dropped once a mounted disk supersedes it (see <see cref="MergeProfiles"/>).
    /// Only touched on the UI thread.
    /// </summary>
    private readonly List<DiskProfile> _unmountedProfiles = [];

    /// <summary>
    /// Initializes a new <see cref="MainViewModel"/> using the supplied mount manager and settings store.
    /// </summary>
    /// <param name="mountManager">The application-wide mount manager.</param>
    /// <param name="settingsStore">The settings store used by the Settings dialog.</param>
    /// <param name="logger">Logger resolved from the DI container built in <see cref="App"/>.</param>
    /// <param name="savedProfiles">
    /// The disk profiles of the settings the caller loaded at startup, none of which is mounted yet.
    /// </param>
    public MainViewModel(
        MountManager mountManager,
        SettingsStore settingsStore,
        ILogger<MainViewModel> logger,
        IEnumerable<DiskProfile> savedProfiles)
    {
        _mountManager = mountManager;
        _settingsStore = settingsStore;
        _logger = logger;

        // Before anything can call SaveSettings (startup dialogs, tray commands, the auto-mount
        // loop): none of these profiles is mounted yet.
        RetainSavedProfiles(savedProfiles);

        StatusText = Loc.Get("Status.Ready");

        // Surface an empty-list flag for the main window's empty-state guidance overlay.
        // Subscribing to CollectionChanged covers every add/remove path in one place.
        Disks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        CreateDiskCommand = new(_ => ExecuteCreateDisk());
        ImportDiskCommand = new(_ => ExecuteImportDisk(), _ => !BusyOverlay.IsBusy);
        ImportArchiveCommand = new(_ => ExecuteImportArchive(), _ => !BusyOverlay.IsBusy);
        EditDiskCommand = new(
            p => ExecuteEditDisk(ResolveTarget(p)),
            p => ResolveTarget(p) != null);
        ExitCommand = new(_ => ExecuteExit());
        UnmountCommand = new(
            p => ExecuteUnmount(ResolveTarget(p)),
            p => ResolveTarget(p) != null);
        SaveImageCommand = new(
            p => ExecuteSaveImage(ResolveTarget(p)),
            p => !BusyOverlay.IsBusy && ResolveTarget(p) != null);
        CreateSnapshotNowCommand = new(
            p => ExecuteCreateSnapshotNow(ResolveTarget(p)),
            p =>
            {
                var vm = ResolveTarget(p);
                return !BusyOverlay.IsBusy && vm is { SnapshotsEnabled: true, HasImagePath: true };
            });
        FormatDiskCommand = new(
            p => ExecuteFormatDisk(ResolveTarget(p)),
            p =>
            {
                var vm = ResolveTarget(p);
                return vm is { Disk.Options.ReadOnly: false };
            });
        CloneDiskCommand = new(
            p => ExecuteCloneDisk(ResolveTarget(p)),
            p => !BusyOverlay.IsBusy && ResolveTarget(p) != null);
        RestoreSnapshotCommand = new(
            p => ExecuteRestoreSnapshot(ResolveTarget(p)),
            p =>
            {
                var vm = ResolveTarget(p);
                return vm is { IsReadOnly: false, HasImagePath: true };
            });
        ViewDiskContentsCommand = new(
            p => ExecuteViewDiskContents(ResolveTarget(p)),
            p => ResolveTarget(p) != null);
        AnalyzeSpaceCommand = new(
            p => ExecuteAnalyzeSpace(ResolveTarget(p)),
            p => ResolveTarget(p) != null);
        RefreshCommand = new(_ => RefreshAll());
        ResetTempDirsCommand = new(_ => ExecuteResetTempDirs());
        ToggleTempDirCommand = new(
            p => ExecuteToggleTempDir(ResolveTarget(p)),
            p =>
            {
                var vm = ResolveTarget(p);
                return vm is { Disk.Options.ReadOnly: false };
            });
        SettingsCommand = new(_ => ExecuteSettings());
        AboutCommand = new(_ => ExecuteAbout());

        RefreshAvailableMemory();
        _memoryRefreshTimer = new()
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _memoryRefreshTimer.Tick += (_, _) =>
        {
            // Only the main window's status bar and the tray tooltip show this; skip the
            // GlobalMemoryStatusEx call while neither is visible. The tooltip force-refreshes
            // via RefreshForTrayTooltip() right before it's shown, so it never reads stale data.
            if (Application.Current?.MainWindow is { IsVisible: true })
            {
                RefreshAvailableMemory();
            }
        };
        _memoryRefreshTimer.Start();

        _diskActivityStatusTimer = new()
        {
            Interval = DiskActivityStatusDuration
        };
        _diskActivityStatusTimer.Tick += (_, _) =>
        {
            _diskActivityStatusTimer.Stop();
            StatusText = Loc.Get("Status.Ready");
        };

        LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Re-localizes the status bar and tray tooltip text after a language switch. Most
    /// <see cref="StatusText"/> assignments are transient (they revert to <c>Status.Ready</c>
    /// within <see cref="DiskActivityStatusDuration"/>) or get re-issued by the very next disk
    /// event, so only the two states that could otherwise sit stale indefinitely — a sticky
    /// problem report and the plain "ready/idle" text — are refreshed here.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_stickyStatus is null)
        {
            StatusText = Loc.Get("Status.Ready");
        }

        RefreshAvailableMemory();
        OnPropertyChanged(nameof(NoDisksMountedText));
        OnPropertyChanged(nameof(LoadingDisksText));
    }

    /// <summary>
    /// Occurs when the user asks the application to exit.
    /// </summary>
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
    /// Gets the localized "no mounted disks" message shown in the tray tooltip. A plain
    /// getter (not a <c>DynamicResource</c>) because the tooltip's content lives inside a
    /// <see cref="System.Windows.Controls.Primitives.Popup"/> that's disconnected from the
    /// visual tree while closed, where <c>DynamicResource</c> invalidation doesn't reach it;
    /// <see cref="OnLanguageChanged"/> raises <see cref="PropertyChanged"/> for this property
    /// explicitly instead.
    /// </summary>
    public string NoDisksMountedText => Loc.Get("Tray.NoDisksMounted");

    /// <summary>
    /// Gets the localized "loading disks" message shown in the tray tooltip while disks are
    /// still being auto-mounted. See <see cref="NoDisksMountedText"/> for why this isn't a
    /// <c>DynamicResource</c>.
    /// </summary>
    public string LoadingDisksText => Loc.Get("Tray.LoadingDisks");

    /// <summary>
    /// Gets a localized, human-readable description of the currently available physical
    /// system memory (e.g. "1.2 GB available"), refreshed every 2 seconds.
    /// </summary>
    public string AvailableMemoryFormatted
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged(nameof(AvailableMemoryFormatted));
        }
    } = string.Empty;

    /// <summary>
    /// Gets the busy/progress overlay state shown during a long-running disk operation
    /// (image save, archive import, export) triggered from this view model.
    /// </summary>
    public BusyOverlayViewModel BusyOverlay { get; } = new();

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
    /// Gets a value indicating whether no disks are currently mounted. Bound by the main
    /// window to show its empty-state guidance overlay.
    /// </summary>
    public bool IsEmpty => Disks.Count == 0;

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
    /// Gets the overall progress fraction (0-1) of the final disk save(s) performed while
    /// <see cref="IsExiting"/> is <c>true</c>. Driven by <see cref="ReportExitSaveProgress"/>.
    /// </summary>
    public double ExitSaveProgress
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged(nameof(ExitSaveProgress));
        }
    }

    /// <summary>
    /// Gets the status text shown above the exit-saving progress bar.
    /// </summary>
    public string ExitSaveStatusText
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged(nameof(ExitSaveStatusText));
        }
    } = string.Empty;

    /// <summary>
    /// Gets the "bytes so far / total bytes" detail text shown below <see cref="ExitSaveStatusText"/>,
    /// driven by <see cref="ReportExitSaveProgress"/>.
    /// </summary>
    public string ExitSaveDetailText
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged(nameof(ExitSaveDetailText));
        }
    } = string.Empty;

    /// <summary>
    /// Gets the command that formats (clears all content from) the selected disk.
    /// </summary>
    public RelayCommand FormatDiskCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that opens the "Import Archive" flow: pick an archive file (zip, 7z,
    /// rar, tar, or any other format SharpCompress can read) and mount its contents as a new
    /// read-only disk.
    /// </summary>
    public RelayCommand ImportArchiveCommand
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

            if (value)
            {
                ExitSaveProgress = 0;
                ExitSaveStatusText = Loc.Get("Msg.ExitSaving");
                ExitSaveDetailText = string.Empty;
            }
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
    /// Gets the command that writes a timestamped snapshot for the selected disk right now,
    /// independent of whether the user was otherwise about to save the image.
    /// </summary>
    public RelayCommand CreateSnapshotNowCommand
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

    /// <summary>
    /// The update checker used by the About dialog's "Check for Updates" button. Set by
    /// <see cref="ManagedDrive.App.App"/> after construction, since <see cref="UpdateCheckService"/>
    /// itself depends on services constructed after this view model. <c>null</c> disables the
    /// button (the dialog treats a missing service as a no-op).
    /// </summary>
    public UpdateCheckService? UpdateCheckService
    {
        get; set;
    }

    /// <summary>
    /// Gets the command that opens a read-only view of the selected disk's file/directory
    /// tree and per-node space usage.
    /// </summary>
    public RelayCommand ViewDiskContentsCommand
    {
        get;
    }

    /// <summary>
    /// Gets the command that opens the space-usage analysis (treemap and top lists) of the
    /// selected disk.
    /// </summary>
    public RelayCommand AnalyzeSpaceCommand
    {
        get;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;
        _memoryRefreshTimer.Stop();
        _diskActivityStatusTimer.Stop();

        foreach (var vm in Disks)
        {
            vm.Dispose();
        }
    }

    /// <summary>
    /// Exits the application without the interactive confirmation dialog used by
    /// <see cref="ExecuteExit"/> — for callers (the CLI) that have already committed to exiting
    /// and have no dialog to show. Still resets TEMP first if it points at a mounted RAM disk,
    /// same as the confirmed interactive path.
    /// </summary>
    public void ExitWithoutConfirmation()
    {
        _logger.LogInformation("Exit requested via CLI.");

        if (TempDirCompatChecker.IsTempOnAnyDisk(Disks))
        {
            TempDirResetService.Reset();
        }

        ExitRequested?.Invoke(this, EventArgs.Empty);
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

    private void RefreshAvailableMemory()
    {
        var availableBytes = SystemMemoryInfo.GetAvailablePhysicalBytes();
        AvailableMemoryFormatted = Loc.Format("Status.AvailableMemory", ByteFormatter.Format(availableBytes));
    }
}
