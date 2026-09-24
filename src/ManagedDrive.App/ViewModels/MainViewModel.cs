using ManagedDrive.Cli.Core;
using System.Collections.ObjectModel;
using static ManagedDrive.App.ViewModels.MainViewModelHelpers;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// View model for <see cref="ManagedDrive.App.MainWindow"/>. Manages the collection of active
/// disks and exposes commands for the toolbar and context menu.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan DiskActivityStatusDuration = TimeSpan.FromSeconds(2.5);

    private readonly DispatcherTimer _diskActivityStatusTimer;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _memoryRefreshTimer;
    private readonly MountManager _mountManager;
    private readonly SettingsStore _settingsStore;

    /// <summary>
    /// Saved auto-mount profiles that failed to mount this session (image on an unplugged drive,
    /// cancelled password prompt, drive letter taken, ...). <see cref="SaveSettings"/> keeps
    /// writing them back so one failed startup doesn't permanently delete the disk's profile;
    /// a profile is dropped once a mounted disk supersedes it (see <see cref="MergeProfiles"/>).
    /// Only touched on the UI thread.
    /// </summary>
    private readonly List<DiskProfile> _unmountedProfiles = [];

    /// <summary>
    /// Initializes a new <see cref="MainViewModel"/> using the supplied mount manager and settings store.
    /// </summary>
    /// <param name="mountManager">The application-wide mount manager.</param>
    /// <param name="settingsStore">The settings store used by the Settings dialog.</param>
    /// <param name="logger">Logger resolved from the DI container built in <see cref="App"/>.</param>
    public MainViewModel(MountManager mountManager, SettingsStore settingsStore, ILogger<MainViewModel> logger)
    {
        _mountManager = mountManager;
        _settingsStore = settingsStore;
        _logger = logger;

        StatusText = Loc.Get("Status.Ready");

        // Surface an empty-list flag for the main window's empty-state guidance overlay.
        // Subscribing to CollectionChanged covers every add/remove path in one place.
        Disks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        CreateDiskCommand = new(_ => ExecuteCreateDisk());
        ImportDiskCommand = new(_ => ExecuteImportDisk());
        ImportArchiveCommand = new(_ => ExecuteImportArchive());
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
        CreateSnapshotNowCommand = new(
            p => ExecuteCreateSnapshotNow(p as DiskViewModel ?? SelectedDisk),
            p =>
            {
                var vm = p as DiskViewModel ?? SelectedDisk;
                return vm is { SnapshotsEnabled: true, HasImagePath: true };
            });
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
        ViewDiskContentsCommand = new(
            p => ExecuteViewDiskContents(p as DiskViewModel ?? SelectedDisk),
            p => p is DiskViewModel || SelectedDisk != null);
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
    /// Gets the status bar text.
    /// </summary>
    public string StatusText
    {
        get;
        internal set
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

    /// <inheritdoc />
    public void Dispose()
    {
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

    /// <summary>
    /// Formats the disk currently mounted at <paramref name="mountPoint"/> without any
    /// interactive confirmation, for use by the CLI command channel.
    /// </summary>
    /// <param name="mountPoint">The mount point to format (e.g. <c>"R:"</c>).</param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> if the disk is read-only; or
    /// <c>(false, string.Empty)</c> if no disk is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public Task<(bool Success, string Message)> FormatByMountPointAsync(string mountPoint)
    {
        _logger.LogInformation("CLI format requested for {MountPoint}.", mountPoint);

        var vm = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            return Task.FromResult((false, string.Empty));
        }

        if (!vm.Disk.Format())
        {
            _logger.LogWarning("CLI format failed for {MountPoint}: disk is read-only.", mountPoint);
            return Task.FromResult((false, Loc.Get("Msg.FormatDiskReadOnly")));
        }

        vm.Refresh();
        StatusText = Loc.Format("Status.FormatDisk", mountPoint);
        _logger.LogInformation("CLI format completed for {MountPoint}.", mountPoint);
        return Task.FromResult((true, StatusText));
    }

    /// <summary>
    /// Returns a <see cref="DiskProfile"/> snapshot for every currently active disk.
    /// </summary>
    /// <returns>
    /// A sequence of <see cref="DiskProfile"/> representing all mounted disks.
    /// </returns>
    public IEnumerable<DiskProfile> GetProfiles() => Disks.Select(vm => ToProfile(vm.Disk.Options));

    /// <summary>
    /// Combines the profiles of the mounted disks with saved profiles that failed to mount, so the
    /// latter survive a settings save. A not-mounted profile is dropped when a mounted disk
    /// supersedes it: same mount point (the drive letter now belongs to that disk, and keeping
    /// both would make them race for it on the next startup), or same backing image or archive
    /// (the same disk was mounted again, possibly at another letter).
    /// </summary>
    /// <param name="mounted">Profiles of the currently mounted disks; always kept, in order.</param>
    /// <param name="unmounted">Saved profiles that failed to mount.</param>
    /// <returns>
    /// <paramref name="mounted"/> followed by every profile in <paramref name="unmounted"/> that
    /// isn't superseded by one of them.
    /// </returns>
    internal static List<DiskProfile> MergeProfiles(IReadOnlyList<DiskProfile> mounted, IEnumerable<DiskProfile> unmounted)
    {
        var merged = mounted.ToList();
        foreach (var profile in unmounted)
        {
            if (!merged.Any(p => Supersedes(p, profile)))
            {
                merged.Add(profile);
            }
        }
        return merged;

        static bool Supersedes(DiskProfile kept, DiskProfile candidate) =>
            string.Equals(kept.MountPoint, candidate.MountPoint, StringComparison.OrdinalIgnoreCase) ||
            SamePath(kept.PersistImagePath, candidate.PersistImagePath) ||
            SamePath(kept.SourceArchivePath, candidate.SourceArchivePath);

        static bool SamePath(string? a, string? b) =>
            a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps a live disk's <see cref="DiskOptions"/> to its persistable <see cref="DiskProfile"/>
    /// counterpart. Inverse of <see cref="ProfileToOptions"/>; kept as a standalone pure function
    /// (rather than inlined in <see cref="GetProfiles"/>) so both directions of this hand-written
    /// field mapping can be round-trip tested independently of any live <see cref="MainViewModel"/>
    /// or mounted disk.
    /// </summary>
    internal static DiskProfile ToProfile(DiskOptions options) => new()
    {
        MountPoint = options.MountPoint,
        VolumeLabel = options.VolumeLabel,
        CapacityBytes = options.CapacityBytes,
        ReadOnly = options.ReadOnly,
        AutoMount = options.AutoMount,
        PersistImagePath = options.PersistImagePath,
        SourceArchivePath = options.SourceArchivePath,
        AutoSaveIntervalMinutes = options.AutoSaveIntervalMinutes,
        CompressionLevel = options.CompressionLevel,
        CustomZstdLevel = options.CustomZstdLevel,
        MaxSnapshotCount = options.MaxSnapshotCount,
        MaxSnapshotSizeBytes = options.MaxSnapshotSizeBytes,
        HighUsageWarnPercent = options.HighUsageWarnPercent,
        SaveImageOnExit = options.SaveImageOnExit,
    };

    /// <summary>
    /// Mounts the contents of an archive file as a new read-only disk, for use by the CLI
    /// command channel (<c>mdrive mount-archive</c>). Mirrors <see cref="MountImageAsync"/> but
    /// sources content from <see cref="ArchiveNodeMapBuilder.PeekArchive"/> instead of
    /// <see cref="DiskImageSerializer.PeekHeader"/>, and forces the disk read-only since none of
    /// the supported archive formats support writing changes back.
    /// </summary>
    /// <param name="archivePath">Path to an existing archive file.</param>
    /// <param name="mountPoint">
    /// The drive letter to mount at (e.g. <c>"R:"</c>), or <c>null</c> to automatically pick the
    /// first free letter searching from <c>Z:</c> down to <c>D:</c> (used when the caller — e.g.
    /// the Explorer right-click context menu — has no way to prompt for one).
    /// </param>
    /// <param name="overrides">
    /// Per-field values the user explicitly passed via CLI flags; only
    /// <see cref="CliMountOverrides.AutoMount"/> applies here — every other field is meaningless
    /// for an archive-sourced disk and is ignored even if set.
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise (mount point already in use, no free drive letter, archive already mounted by
    /// another disk, invalid archive file, or a mount failure).
    /// </returns>
    public async Task<(bool Success, string Message)> MountArchiveAsync(string archivePath, string? mountPoint, CliMountOverrides overrides)
    {
        _logger.LogInformation("CLI mount-archive requested: {ArchivePath} -> {MountPoint}.", archivePath, mountPoint ?? "(auto)");

        if (mountPoint == null)
        {
            mountPoint = FindFreeDriveLetter();
            if (mountPoint == null)
            {
                return (false, Loc.Get("Val.NoFreeDriveLetter"));
            }
        }
        else if (Disks.Any(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, Loc.Format("Val.MountPointAlreadyMounted", mountPoint));
        }

        var otherDisks = GetOtherDiskOptions(excluding: null);
        if (!MountPointValidator.TryValidateDirectoryMountPoint(mountPoint, otherDisks.Select(d => d.MountPoint), out var mountPointError))
        {
            return (false, mountPointError!);
        }

        if (IsPathInUse(otherDisks, archivePath, d => d.SourceArchivePath))
        {
            return (false, Loc.Get("Val.ArchivePathInUse"));
        }

        ulong capacityBytes;
        string volumeLabel;
        try
        {
            ArchiveNodeMapBuilder.PeekArchive(archivePath, out capacityBytes, out volumeLabel);
        }
        catch (InvalidDataException)
        {
            return (false, Loc.Get("Val.ImportInvalidArchive"));
        }

        var savedProfile = _settingsStore.Load().Disks
            .FirstOrDefault(p => p.SourceArchivePath != null &&
                string.Equals(p.SourceArchivePath, archivePath, StringComparison.OrdinalIgnoreCase));

        var options = MountOptionsFactory.BuildArchiveOptions(
            savedProfile != null ? ProfileToOptions(savedProfile) : null,
            mountPoint, archivePath, capacityBytes, volumeLabel, overrides.AutoMount);

        try
        {
            var disk = await Task.Run(() => _mountManager.Mount(options));
            AddDiskSorted(new(disk));
            SaveSettings();
            StatusText = Loc.Format("Status.MountedWithCapacity", disk.MountPoint, options.VolumeLabel, options.CapacityBytes / (1024 * 1024));
            _logger.LogInformation("CLI mount-archive succeeded: {ArchivePath} -> {MountPoint}.", archivePath, disk.MountPoint);
            Process.Start("explorer.exe", disk.MountPoint);
            return (true, StatusText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI mount-archive failed for {ArchivePath}.", archivePath);
            return (false, Loc.Format("Msg.MountFailed", ex.Message));
        }
    }

    /// <summary>
    /// Mounts a disk from a saved <see cref="DiskProfile"/> and adds it to the list.
    /// Errors are surfaced via <see cref="StatusText"/>.
    /// </summary>
    /// <param name="profile">The profile to mount.</param>
    /// <param name="progress">An optional progress reporter.</param>
    /// <returns>
    /// <c>true</c> if the disk was mounted successfully; <c>false</c> if mounting failed
    /// (the failure reason is surfaced via <see cref="StatusText"/>).
    /// </returns>
    public async Task<bool> MountFromProfileAsync(DiskProfile profile, IProgress<double>? progress = null)
    {
        _logger.LogInformation("Auto-mounting saved profile {MountPoint}.", profile.MountPoint);
        var options = ProfileToOptions(profile);

        try
        {
            var disk = await MountWithPasswordRetryAsync(options, progress: progress);
            if (disk is null)
            {
                _logger.LogWarning("Auto-mount failed for {MountPoint}.", profile.MountPoint);
                StatusText = Loc.Format("Status.AutoMountFailed", profile.MountPoint, Loc.Get("Status.MountFailed"));
                ResetTempIfPointingAt(profile.MountPoint);
                _unmountedProfiles.Add(profile);
                return false;
            }

            AddDiskSorted(new(disk));
            StatusText = Loc.Format("Status.Mounted", disk.MountPoint, profile.VolumeLabel);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-mount failed for {MountPoint}.", profile.MountPoint);
            StatusText = Loc.Format("Status.AutoMountFailed", profile.MountPoint, ex.Message);
            ResetTempIfPointingAt(profile.MountPoint);
            _unmountedProfiles.Add(profile);
            return false;
        }
    }

    /// <summary>
    /// Mounts an existing <c>.mdr</c> disk image at <paramref name="mountPoint"/>, without any
    /// interactive dialogs, for use by the CLI command channel. Capacity and volume label are
    /// read directly from the image header (mirrors <c>ExecuteImportDisk</c>'s non-interactive
    /// steps). If a saved profile referencing this exact <paramref name="imagePath"/> is found in
    /// settings, its other options (read-only, auto-mount, auto-save interval, compression level,
    /// snapshot limits, high-usage threshold) are reused instead of falling back to
    /// <see cref="DiskOptions"/> defaults; <paramref name="mountPoint"/> and the header-derived
    /// capacity/label always win over the profile's stored values. Any non-null field on
    /// <paramref name="overrides"/> wins over both the saved profile and the built-in default for
    /// that field.
    /// </summary>
    /// <param name="imagePath">Path to an existing <c>.mdr</c> image file.</param>
    /// <param name="mountPoint">The drive letter to mount at (e.g. <c>"R:"</c>).</param>
    /// <param name="overrides">
    /// Per-field values the user explicitly passed via CLI flags; <c>null</c> fields defer to the
    /// saved profile or built-in default.
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise (mount point already in use, image already in use by another disk, invalid
    /// image file, or a mount failure).
    /// </returns>
    public async Task<(bool Success, string Message)> MountImageAsync(string imagePath, string mountPoint, CliMountOverrides overrides)
    {
        _logger.LogInformation("CLI mount requested: {ImagePath} -> {MountPoint}.", imagePath, mountPoint);

        if (Disks.Any(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, Loc.Format("Val.MountPointAlreadyMounted", mountPoint));
        }

        var otherDisks = GetOtherDiskOptions(excluding: null);
        if (!MountPointValidator.TryValidateDirectoryMountPoint(mountPoint, otherDisks.Select(d => d.MountPoint), out var mountPointError))
        {
            return (false, mountPointError!);
        }

        if (IsPathInUse(otherDisks, imagePath, d => d.PersistImagePath))
        {
            return (false, Loc.Get("Val.ImagePathInUse"));
        }

        ulong capacityBytes;
        string volumeLabel;
        bool isEncrypted;
        try
        {
            DiskImageSerializer.PeekHeader(imagePath, out capacityBytes, out volumeLabel, out isEncrypted);
        }
        catch (InvalidDataException)
        {
            return (false, Loc.Get("Val.ImportInvalidImage"));
        }

        if (isEncrypted && overrides.Password is null)
        {
            return (false, Loc.Get("Val.CliPasswordRequired"));
        }

        var savedProfile = _settingsStore.Load().Disks
            .FirstOrDefault(p => p.PersistImagePath != null &&
                string.Equals(p.PersistImagePath, imagePath, StringComparison.OrdinalIgnoreCase));

        var options = MountOptionsFactory.BuildImageOptions(
            savedProfile != null ? ProfileToOptions(savedProfile) : null,
            mountPoint, imagePath, capacityBytes, volumeLabel,
            new()
            {
                ReadOnly = overrides.ReadOnly,
                AutoMount = overrides.AutoMount,
                AutoSaveIntervalMinutes = overrides.AutoSaveIntervalMinutes,
                CompressionLevel = overrides.CompressionLevel is { } compressionLevel
                    ? (Core.Mounting.ImageCompressionLevel)compressionLevel
                    : null,
                CustomZstdLevel = overrides.CustomZstdLevel,
                MaxSnapshotCount = overrides.MaxSnapshotCount,
                MaxSnapshotSizeBytes = overrides.MaxSnapshotSizeBytes,
                HighUsageWarnPercent = overrides.HighUsageWarnPercent,
            });

        try
        {
            var disk = await Task.Run(() => _mountManager.Mount(options, overrides.Password));
            AddDiskSorted(new(disk));
            SaveSettings();
            StatusText = Loc.Format("Status.MountedWithCapacity", disk.MountPoint, options.VolumeLabel, options.CapacityBytes / (1024 * 1024));
            _logger.LogInformation("CLI mount succeeded: {ImagePath} -> {MountPoint}.", imagePath, disk.MountPoint);
            return (true, StatusText);
        }
        catch (ImagePasswordRequiredException)
        {
            _logger.LogWarning("CLI mount failed for {ImagePath}: password required.", imagePath);
            return (false, Loc.Get("Val.CliPasswordRequired"));
        }
        catch (ImagePasswordIncorrectException)
        {
            _logger.LogWarning("CLI mount failed for {ImagePath}: incorrect password.", imagePath);
            return (false, Loc.Get("Val.CliPasswordIncorrect"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI mount failed for {ImagePath}.", imagePath);
            return (false, Loc.Format("Msg.MountFailed", ex.Message));
        }
    }

    /// <summary>
    /// Creates a brand-new, empty RAM disk at <paramref name="mountPoint"/>, without any
    /// interactive dialogs, for use by the CLI command channel.
    /// </summary>
    /// <param name="mountPoint">The drive letter to mount at (e.g. <c>"R:"</c>).</param>
    /// <param name="capacityBytes">The disk's capacity in bytes.</param>
    /// <param name="volumeLabel">The volume label, or <c>null</c> to use the built-in default.</param>
    /// <param name="imagePath">
    /// Optional path to persist the disk to (created on first save); <c>null</c> for a
    /// memory-only disk that is discarded on unmount.
    /// </param>
    /// <param name="password">Optional password to encrypt <paramref name="imagePath"/> with.</param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise (mount point already in use, invalid capacity, or image path collision).
    /// </returns>
    public async Task<(bool Success, string Message)> CreateByOptionsAsync(
        string mountPoint, ulong capacityBytes, string? volumeLabel, string? imagePath, string? password)
    {
        _logger.LogInformation("CLI create requested: {MountPoint}, capacity {CapacityBytes} bytes.", mountPoint, capacityBytes);

        if (Disks.Any(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, Loc.Format("Val.MountPointAlreadyMounted", mountPoint));
        }

        var otherDisks = GetOtherDiskOptions(excluding: null);
        if (!MountPointValidator.TryValidateDirectoryMountPoint(mountPoint, otherDisks.Select(d => d.MountPoint), out var mountPointError))
        {
            return (false, mountPointError!);
        }

        if (capacityBytes == 0)
        {
            return (false, Loc.Get("Val.CliBadCapacity"));
        }

        imagePath = string.IsNullOrWhiteSpace(imagePath) ? null : imagePath.Trim();
        if (imagePath != null)
        {
            if (!CreateDiskOptionsBuilder.IsValidImagePath(imagePath))
            {
                return (false, Loc.Get("Val.BadImagePath"));
            }

            var availability = CreateDiskOptionsBuilder.ValidateImagePathAvailable(imagePath, otherDisks);
            if (availability != CreateDiskValidationError.None)
            {
                return (false, availability == CreateDiskValidationError.ImagePathIsSnapshot
                    ? Loc.Get("Val.ImagePathIsSnapshot")
                    : Loc.Get("Val.ImagePathInUse"));
            }
        }

        var options = new DiskOptions
        {
            MountPoint = mountPoint,
            VolumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "RAM Disk" : volumeLabel.Trim(),
            CapacityBytes = capacityBytes,
            PersistImagePath = imagePath,
        };

        try
        {
            var disk = await Task.Run(() => _mountManager.Mount(options, password));
            AddDiskSorted(new(disk));
            SaveSettings();
            StatusText = Loc.Format("Status.MountedWithCapacity", disk.MountPoint, options.VolumeLabel, options.CapacityBytes / (1024 * 1024));
            _logger.LogInformation("CLI create succeeded: {MountPoint}.", disk.MountPoint);
            return (true, StatusText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI create failed for {MountPoint}.", mountPoint);
            return (false, Loc.Format("Msg.MountFailed", ex.Message));
        }
    }

    /// <summary>
    /// Applies non-destructive option changes (capacity, volume label, auto-save interval) to the
    /// disk currently mounted at <paramref name="mountPoint"/>, for use by the CLI command
    /// channel. Mirrors <c>ExecuteEditDisk</c>'s live (non-remounting) path — drive-letter and
    /// read-only changes, which require a full remount, are out of scope for this method.
    /// </summary>
    /// <param name="mountPoint">The mount point to edit, e.g. <c>"R:"</c>.</param>
    /// <param name="capacityBytes">The new capacity in bytes, or <c>null</c> to keep the current value.</param>
    /// <param name="volumeLabel">The new volume label, or <c>null</c> to keep the current value.</param>
    /// <param name="autoSaveIntervalMinutes">
    /// The new auto-save interval in minutes, or <c>null</c> to keep the current value. Ignored
    /// when <paramref name="disableAutoSave"/> is <c>true</c>.
    /// </param>
    /// <param name="disableAutoSave"><c>true</c> to disable auto-save entirely.</param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise — including an invalid capacity or a capacity reduction below current usage; or
    /// <c>(false, string.Empty)</c> if no disk is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message)> EditByMountPointAsync(
        string mountPoint, ulong? capacityBytes, string? volumeLabel, uint? autoSaveIntervalMinutes, bool disableAutoSave)
    {
        _logger.LogInformation("CLI edit requested for {MountPoint}.", mountPoint);

        var vm = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            return (false, string.Empty);
        }

        if (capacityBytes == 0)
        {
            return (false, Loc.Get("Val.CliBadCapacity"));
        }

        var newOptions = vm.Disk.Options with
        {
            CapacityBytes = capacityBytes ?? vm.Disk.Options.CapacityBytes,
            VolumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? vm.Disk.Options.VolumeLabel : volumeLabel.Trim(),
            AutoSaveIntervalMinutes = disableAutoSave ? null : autoSaveIntervalMinutes ?? vm.Disk.Options.AutoSaveIntervalMinutes,
        };

        string? error = null;
        var success = await Task.Run(() => vm.Disk.TryApplyOptions(newOptions, out error));
        if (!success)
        {
            _logger.LogWarning("CLI edit failed for {MountPoint}: {Error}", mountPoint, error);
            return (false, error ?? string.Empty);
        }

        vm.Refresh();
        SaveSettings();
        StatusText = Loc.Format("Status.MountedWithCapacity", vm.MountPoint, newOptions.VolumeLabel, newOptions.CapacityBytes / (1024 * 1024));
        _logger.LogInformation("CLI edit completed for {MountPoint}.", mountPoint);
        return (true, StatusText);
    }

    /// <summary>
    /// Replaces the contents of the disk mounted at <paramref name="targetMountPoint"/> with a
    /// copy of the disk mounted at <paramref name="sourceMountPoint"/>'s current contents, for use
    /// by the CLI command channel. Mirrors <c>ExecuteCloneDisk</c>'s clone-to-mounted-disk branch,
    /// but without the confirmation dialog.
    /// </summary>
    /// <param name="sourceMountPoint">The mount point to copy content from, e.g. <c>"R:"</c>.</param>
    /// <param name="targetMountPoint">The mount point to overwrite, e.g. <c>"S:"</c>.</param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise — including either mount point not being mounted, the target being read-only, or
    /// the target's capacity being smaller than the source's used bytes.
    /// </returns>
    public Task<(bool Success, string Message)> CloneByMountPointAsync(string sourceMountPoint, string targetMountPoint)
    {
        _logger.LogInformation("CLI clone requested: {Source} -> {Target}.", sourceMountPoint, targetMountPoint);

        var source = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, sourceMountPoint, StringComparison.OrdinalIgnoreCase));
        if (source == null)
        {
            return Task.FromResult((false, Loc.Format("Msg.CliMountPointNotMounted", sourceMountPoint)));
        }

        var target = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, targetMountPoint, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return Task.FromResult((false, Loc.Format("Msg.CliMountPointNotMounted", targetMountPoint)));
        }

        if (target == source)
        {
            return Task.FromResult((false, Loc.Get("Val.CliCloneTargetIsSource")));
        }

        if (target.IsReadOnly)
        {
            return Task.FromResult((false, Loc.Format("Val.CliCloneTargetReadOnly", targetMountPoint)));
        }

        if (!target.Disk.TryCloneFrom(source.Disk, out var error))
        {
            _logger.LogWarning("CLI clone failed: {Source} -> {Target}: {Error}", sourceMountPoint, targetMountPoint, error);
            return Task.FromResult((false, error ?? string.Empty));
        }

        target.Refresh();
        _logger.LogInformation("CLI clone completed: {Source} -> {Target}.", sourceMountPoint, targetMountPoint);
        return Task.FromResult((true, Loc.Format("Status.DiskCloned", sourceMountPoint, targetMountPoint)));
    }

    /// <summary>
    /// Saves the disk currently mounted at <paramref name="mountPoint"/> to its backing image
    /// file immediately, for use by the CLI command channel.
    /// </summary>
    /// <param name="mountPoint">The mount point to save (e.g. <c>"R:"</c>).</param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> if no image path is configured
    /// or the save failed; or <c>(false, string.Empty)</c> if no disk is currently mounted at
    /// <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message)> SaveByMountPointAsync(string mountPoint)
    {
        _logger.LogInformation("CLI save requested for {MountPoint}.", mountPoint);

        var vm = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            return (false, string.Empty);
        }

        if (vm.Disk.Options.PersistImagePath == null)
        {
            return (false, Loc.Get("Msg.SaveImageNoPath"));
        }

        try
        {
            await Task.Run(() => vm.Disk.SaveToImageWithSnapshot());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI save failed for {MountPoint}.", mountPoint);
            return (false, Loc.Format("Msg.SaveImageFailed", ex.Message));
        }

        StatusText = Loc.Format("Status.ImageSaved", mountPoint);
        _logger.LogInformation("CLI save completed for {MountPoint}.", mountPoint);
        return (true, StatusText);
    }

    /// <summary>
    /// Sets or removes the encryption password of the disk currently mounted at
    /// <paramref name="mountPoint"/>, for use by the CLI command channel. Takes effect on the
    /// next save — mirrors <c>ExecuteEditDisk</c>'s live (non-remounting) password-change path,
    /// since <see cref="RamDisk.SetPassword"/> doesn't require a remount by itself.
    /// </summary>
    /// <param name="mountPoint">The mount point to change, e.g. <c>"R:"</c>.</param>
    /// <param name="newPassword">The new password, or <see langword="null"/> to remove protection.</param>
    /// <returns>
    /// <c>(true, message)</c> on success; or <c>(false, string.Empty)</c> if no disk is currently
    /// mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public Task<(bool Success, string Message)> SetPasswordByMountPointAsync(string mountPoint, string? newPassword)
    {
        _logger.LogInformation("CLI set-password requested for {MountPoint}.", mountPoint);

        var vm = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            return Task.FromResult((false, string.Empty));
        }

        vm.Disk.SetPassword(newPassword);
        _logger.LogInformation("CLI set-password completed for {MountPoint}.", mountPoint);
        return Task.FromResult((true, newPassword is null
            ? Loc.Format("Status.PasswordRemoved", mountPoint)
            : Loc.Format("Status.PasswordSet", mountPoint)));
    }

    /// <summary>
    /// Lists the immediate children of <paramref name="path"/> on the disk currently mounted at
    /// <paramref name="mountPoint"/>, for use by the CLI command channel.
    /// </summary>
    /// <param name="mountPoint">The mount point to list, e.g. <c>"R:"</c>.</param>
    /// <param name="path">The directory to list; <c>null</c> or empty lists the root.</param>
    /// <returns>
    /// <c>(true, string.Empty, entries)</c> on success (an empty list when the directory has no
    /// children); <c>(false, message, null)</c> if <paramref name="path"/> doesn't exist or names
    /// a file; or <c>(false, string.Empty, null)</c> if no disk is currently mounted at
    /// <paramref name="mountPoint"/>.
    /// </returns>
    public Task<(bool Success, string Message, IReadOnlyList<CliFileEntry>? Entries)> ListFilesByMountPointAsync(string mountPoint, string? path)
    {
        _logger.LogInformation("CLI ls requested for {MountPoint}, path {Path}.", mountPoint, path);

        var vm = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            return Task.FromResult<(bool, string, IReadOnlyList<CliFileEntry>?)>((false, string.Empty, null));
        }

        var normalizedPath = NormalizeListPath(path);
        var nodes = vm.Disk.GetAllNodes();

        if (normalizedPath != "\\" && !nodes.Any(n =>
            string.Equals(n.Key, normalizedPath, StringComparison.OrdinalIgnoreCase) && n.Value.IsDirectory))
        {
            return Task.FromResult<(bool, string, IReadOnlyList<CliFileEntry>?)>((false, Loc.Format("Msg.CliPathNotFound", normalizedPath), null));
        }

        var entries = nodes
            .Where(n => n.Key != "\\" && string.Equals(GetParentPath(n.Key), normalizedPath, StringComparison.OrdinalIgnoreCase))
            .Select(n => new CliFileEntry(n.Value.LeafName, n.Value.IsDirectory, n.Value.FileInfo.FileSize))
            .ToList();

        return Task.FromResult<(bool, string, IReadOnlyList<CliFileEntry>?)>((true, string.Empty, entries));
    }

    /// <summary>
    /// Normalizes a CLI-supplied directory path into the backslash-rooted form used by
    /// <see cref="FileNode.FilePath"/> (e.g. <c>"Folder"</c> or <c>"/Folder"</c> both become
    /// <c>"\Folder"</c>), for <see cref="ListFilesByMountPointAsync"/>.
    /// </summary>
    /// <param name="path">The raw path, or <c>null</c>/empty for the root.</param>
    /// <returns>The normalized, backslash-rooted path with no trailing separator (except the root itself).</returns>
    private static string NormalizeListPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "\\";
        }

        var normalized = path.Replace('/', '\\').Trim();
        if (!normalized.StartsWith('\\'))
        {
            normalized = "\\" + normalized;
        }

        return normalized.Length > 1 ? normalized.TrimEnd('\\') : normalized;
    }

    /// <summary>
    /// Returns the parent directory path of <paramref name="nodePath"/> (e.g. <c>"\Folder\File.txt"</c>
    /// → <c>"\Folder"</c>), for <see cref="ListFilesByMountPointAsync"/>.
    /// </summary>
    /// <param name="nodePath">A full node path as stored in <see cref="FileNode.FilePath"/>.</param>
    /// <returns>The parent directory path, or <c>"\"</c> for a root-level node.</returns>
    private static string GetParentPath(string nodePath)
    {
        var separatorIndex = nodePath.LastIndexOf('\\');
        return separatorIndex <= 0 ? "\\" : nodePath[..separatorIndex];
    }

    /// <summary>
    /// Exports the disk currently mounted at <paramref name="mountPoint"/> to a standalone file,
    /// for use by the CLI command channel. Mirrors <c>ExecuteCloneOrExportDisk</c>'s export
    /// branch but without any dialogs/busy overlay.
    /// </summary>
    /// <param name="mountPoint">The mount point to export, e.g. <c>"R:"</c>.</param>
    /// <param name="outputPath">Destination file path to write the export to.</param>
    /// <param name="archiveFormat"><c>null</c> to export a <c>.mdr</c> image; otherwise the archive container format.</param>
    /// <param name="compressionLevel">Compression level applied to the export.</param>
    /// <param name="password">
    /// Password to encrypt the exported <c>.mdr</c> image with, or <c>null</c> for no encryption.
    /// Ignored when <paramref name="archiveFormat"/> is set.
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> if the export failed; or
    /// <c>(false, string.Empty)</c> if no disk is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message)> ExportByMountPointAsync(
        string mountPoint, string outputPath, Core.Archive.ArchiveExportFormat? archiveFormat, Core.Mounting.ImageCompressionLevel compressionLevel, string? password)
    {
        _logger.LogInformation("CLI export requested: {MountPoint} -> {OutputPath}.", mountPoint, outputPath);

        var vm = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            return (false, string.Empty);
        }

        try
        {
            if (archiveFormat is { } format)
            {
                await Task.Run(() => vm.Disk.ExportToArchive(outputPath, format, compressionLevel));
            }
            else
            {
                await Task.Run(() => vm.Disk.ExportToImage(outputPath, compressionLevel, password));
            }

            StatusText = Loc.Format("Status.DiskExported", vm.MountPoint, outputPath);
            _logger.LogInformation("CLI export completed: {MountPoint} -> {OutputPath}.", mountPoint, outputPath);
            return (true, StatusText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI export failed: {MountPoint} -> {OutputPath}.", mountPoint, outputPath);
            return (false, Loc.Format("Msg.SaveImageFailed", ex.Message));
        }
    }

    /// <summary>
    /// Writes a timestamped snapshot for the disk currently mounted at <paramref name="mountPoint"/>
    /// right now, for use by the CLI command channel. Mirrors <c>ExecuteCreateSnapshotNow</c>, via
    /// the same <see cref="RamDisk.SaveToImageWithSnapshot"/> call, but without the busy overlay.
    /// </summary>
    /// <param name="mountPoint">The mount point to snapshot, e.g. <c>"R:"</c>.</param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> if no image path is configured
    /// or snapshot retention isn't configured (<see cref="DiskOptions.MaxSnapshotCount"/>/
    /// <see cref="DiskOptions.MaxSnapshotSizeBytes"/>); or <c>(false, string.Empty)</c> if no disk
    /// is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message)> CreateSnapshotByMountPointAsync(string mountPoint)
    {
        _logger.LogInformation("CLI snapshot create requested for {MountPoint}.", mountPoint);

        var vm = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            return (false, string.Empty);
        }

        if (!vm.HasImagePath)
        {
            return (false, Loc.Get("Msg.SaveImageNoPath"));
        }

        if (!vm.SnapshotsEnabled)
        {
            return (false, Loc.Get("Msg.CliSnapshotsNotEnabled"));
        }

        try
        {
            await Task.Run(() => vm.Disk.SaveToImageWithSnapshot());
            _logger.LogInformation("CLI snapshot create completed for {MountPoint}.", mountPoint);
            return (true, Loc.Format("Status.ImageSaved", mountPoint));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI snapshot create failed for {MountPoint}.", mountPoint);
            return (false, Loc.Format("Msg.SaveImageFailed", ex.Message));
        }
    }

    /// <summary>
    /// Deletes a single snapshot of the disk currently mounted at <paramref name="mountPoint"/>,
    /// for use by the CLI command channel.
    /// </summary>
    /// <param name="mountPoint">The mount point whose snapshot should be deleted, e.g. <c>"R:"</c>.</param>
    /// <param name="index">
    /// 1-based snapshot index, newest first (1 = newest), re-resolved against the live snapshot
    /// directory at call time — it can refer to a different snapshot than the same index did in an
    /// earlier <see cref="ListSnapshotsByMountPointAsync"/> call if snapshots were added or removed
    /// in between (e.g. by another concurrent <c>mdrive</c> invocation).
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> if no image path is configured,
    /// no snapshots exist, or <paramref name="index"/> is out of range; or
    /// <c>(false, string.Empty)</c> if no disk is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message)> DeleteSnapshotByMountPointAsync(string mountPoint, int index)
    {
        _logger.LogInformation("CLI delete snapshot requested for {MountPoint}, index {Index}.", mountPoint, index);

        var vm = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            return (false, string.Empty);
        }

        if (vm.Disk.Options.PersistImagePath is not { } imagePath)
        {
            return (false, Loc.Get("Msg.SaveImageNoPath"));
        }

        var ordered = await GetOrderedSnapshotsAsync(imagePath);
        if (index < 1 || index > ordered.Count)
        {
            return (false, Loc.Format("Msg.SnapshotIndexOutOfRange", ordered.Count));
        }

        var target = ordered[index - 1];
        await Task.Run(() => vm.Disk.DeleteSnapshot(target.Path));

        _logger.LogInformation("CLI delete snapshot completed for {MountPoint}.", mountPoint);
        return (true, Loc.Format("Status.SnapshotDeleted", mountPoint));
    }

    /// <summary>
    /// Lists the available snapshots of the disk currently mounted at <paramref name="mountPoint"/>,
    /// newest first, for use by the CLI command channel.
    /// </summary>
    /// <param name="mountPoint">The mount point to list snapshots for, e.g. <c>"R:"</c>.</param>
    /// <returns>
    /// <c>(true, string.Empty, snapshots)</c> on success (an empty list when none exist yet);
    /// <c>(false, message, null)</c> if no image path is configured; or
    /// <c>(false, string.Empty, null)</c> if no disk is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message, IReadOnlyList<CliSnapshotInfo>? Snapshots)> ListSnapshotsByMountPointAsync(string mountPoint)
    {
        _logger.LogInformation("CLI snapshot list requested for {MountPoint}.", mountPoint);

        var vm = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            return (false, string.Empty, null);
        }

        if (vm.Disk.Options.PersistImagePath is not { } imagePath)
        {
            return (false, Loc.Get("Msg.SaveImageNoPath"), null);
        }

        var ordered = await GetOrderedSnapshotsAsync(imagePath);
        IReadOnlyList<CliSnapshotInfo> result = ordered
            .Select((s, i) => new CliSnapshotInfo(i + 1, s.TimestampUtc, (ulong)s.SizeBytes))
            .ToList();
        return (true, string.Empty, result);
    }

    /// <summary>
    /// Restores the disk currently mounted at <paramref name="mountPoint"/> from a previously
    /// saved snapshot, for use by the CLI command channel.
    /// </summary>
    /// <param name="mountPoint">The mount point to restore, e.g. <c>"R:"</c>.</param>
    /// <param name="index">
    /// 1-based snapshot index, newest first (1 = newest), re-resolved against the live snapshot
    /// directory at call time — it can refer to a different snapshot than the same index did in an
    /// earlier <see cref="ListSnapshotsByMountPointAsync"/> call if snapshots were added or removed
    /// in between (e.g. by another concurrent <c>mdrive</c> invocation).
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> if no image path is configured,
    /// no snapshots exist, <paramref name="index"/> is out of range, or the restore itself fails
    /// (e.g. read-only disk); or <c>(false, string.Empty)</c> if no disk is currently mounted at
    /// <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message)> RestoreSnapshotByMountPointAsync(string mountPoint, int index)
    {
        _logger.LogInformation("CLI restore snapshot requested for {MountPoint}, index {Index}.", mountPoint, index);

        var vm = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            return (false, string.Empty);
        }

        if (vm.Disk.Options.PersistImagePath is not { } imagePath)
        {
            return (false, Loc.Get("Msg.SaveImageNoPath"));
        }

        var ordered = await GetOrderedSnapshotsAsync(imagePath);
        if (index < 1 || index > ordered.Count)
        {
            return (false, Loc.Format("Msg.SnapshotIndexOutOfRange", ordered.Count));
        }

        var target = ordered[index - 1];
        string? error = null;
        var success = await Task.Run(() => vm.Disk.TryRestoreFromSnapshot(target.Path, out error));

        if (!success)
        {
            _logger.LogWarning("CLI restore snapshot failed for {MountPoint}: {Error}", mountPoint, error);
            return (false, error ?? Loc.Get("Msg.RestoreSnapshotFailedUnknown"));
        }

        vm.Refresh();
        StatusText = Loc.Format("Status.SnapshotRestored", mountPoint);
        _logger.LogInformation("CLI restore snapshot completed for {MountPoint}.", mountPoint);
        return (true, StatusText);
    }

    /// <summary>
    /// Compares a previously saved snapshot of the disk currently mounted at
    /// <paramref name="mountPoint"/> against its current live contents, for use by the CLI command
    /// channel.
    /// </summary>
    /// <param name="mountPoint">The mount point to diff, e.g. <c>"R:"</c>.</param>
    /// <param name="index">
    /// 1-based snapshot index, newest first (1 = newest), re-resolved against the live snapshot
    /// directory at call time — it can refer to a different snapshot than the same index did in an
    /// earlier <see cref="ListSnapshotsByMountPointAsync"/> call if snapshots were added or removed
    /// in between (e.g. by another concurrent <c>mdrive</c> invocation).
    /// </param>
    /// <returns>
    /// <c>(true, string.Empty, diff)</c> on success; <c>(false, message, null)</c> if no image path
    /// is configured or <paramref name="index"/> is out of range; or <c>(false, string.Empty, null)</c>
    /// if no disk is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message, SnapshotManager.SnapshotDiffResult? Diff)> DiffSnapshotByMountPointAsync(string mountPoint, int index)
    {
        _logger.LogInformation("CLI snapshot diff requested for {MountPoint}, index {Index}.", mountPoint, index);

        var vm = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            return (false, string.Empty, null);
        }

        if (vm.Disk.Options.PersistImagePath is not { } imagePath)
        {
            return (false, Loc.Get("Msg.SaveImageNoPath"), null);
        }

        var ordered = await GetOrderedSnapshotsAsync(imagePath);
        if (index < 1 || index > ordered.Count)
        {
            return (false, Loc.Format("Msg.SnapshotIndexOutOfRange", ordered.Count), null);
        }

        var target = ordered[index - 1];
        var diff = await Task.Run(() => vm.Disk.DiffAgainstSnapshot(target.Path));

        _logger.LogInformation("CLI snapshot diff completed for {MountPoint}.", mountPoint);
        return (true, string.Empty, diff);
    }

    /// <summary>
    /// Lists <paramref name="imagePath"/>'s snapshots newest first — the ordering that gives
    /// <see cref="ListSnapshotsByMountPointAsync"/>, <see cref="RestoreSnapshotByMountPointAsync"/>,
    /// and <see cref="DeleteSnapshotByMountPointAsync"/> their shared 1-based, "1 = newest" index.
    /// </summary>
    private static async Task<List<SnapshotManager.SnapshotInfo>> GetOrderedSnapshotsAsync(string imagePath) =>
        (await Task.Run(() => SnapshotManager.ListSnapshots(imagePath)))
        .OrderByDescending(s => s.TimestampUtc)
        .ToList();

    /// <summary>
    /// Unmounts the disk currently mounted at <paramref name="mountPoint"/> without any
    /// interactive confirmation, for use by the CLI command channel. Resets TEMP first if it
    /// currently points into the disk being unmounted.
    /// </summary>
    /// <param name="mountPoint">The mount point to unmount (e.g. <c>"R:"</c>).</param>
    /// <param name="deleteImage">
    /// If <c>true</c>, also deletes the disk's backing image file (and any snapshots) or source
    /// archive file after unmounting.
    /// </param>
    /// <returns>
    /// <c>true</c> if a mounted disk was found and unmounted; <c>false</c> if no disk is
    /// currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<bool> UnmountByMountPointAsync(string mountPoint, bool deleteImage = false)
    {
        _logger.LogInformation("CLI unmount requested for {MountPoint} (deleteImage: {DeleteImage}).", mountPoint, deleteImage);

        var vm = Disks.FirstOrDefault(d => string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            return false;
        }

        var persistImagePath = vm.PersistImagePath;
        var sourceArchivePath = vm.Disk.Options.SourceArchivePath;

        if (vm.IsCurrentTempDir)
        {
            await Task.Run(TempDirResetService.Reset);
        }

        vm.Dispose();
        Disks.Remove(vm);
        await Task.Run(() => _mountManager.Unmount(mountPoint));

        await DeleteDiskImageIfRequestedAsync(deleteImage, persistImagePath, sourceArchivePath);

        SaveSettings();
        StatusText = Loc.Format("Status.Unmounted", mountPoint);
        return true;
    }

    /// <summary>
    /// Forces an immediate, unconditional refresh of per-disk usage and available memory,
    /// bypassing the main-window-visibility skip in <see cref="DiskViewModel.Refresh"/> and
    /// <see cref="RefreshAvailableMemory"/>. Called right before the tray tooltip is shown so it
    /// never displays data that's stale from being paused while the main window was hidden.
    /// </summary>
    internal void RefreshForTrayTooltip()
    {
        RefreshAll();
        RefreshAvailableMemory();
    }

    /// <summary>
    /// Updates <see cref="ExitSaveProgress"/>, <see cref="ExitSaveStatusText"/>, and
    /// <see cref="ExitSaveDetailText"/> during the final on-exit save. Called from
    /// <c>App.ShutdownAsync</c> via
    /// <see cref="ManagedDrive.Core.Mounting.MountManager.Dispose(Action{ManagedDrive.Core.Mounting.RamDisk, double, double, ulong})"/>.
    /// </summary>
    /// <param name="mountPoint">The mount point of the disk currently being saved.</param>
    /// <param name="overallFraction">Overall progress across all disks, in [0, 1].</param>
    /// <param name="diskFraction">Save progress for the disk currently being saved, in [0, 1].</param>
    /// <param name="totalBytes">The disk's total used bytes, for <see cref="ExitSaveDetailText"/>.</param>
    internal void ReportExitSaveProgress(string mountPoint, double overallFraction, double diskFraction, ulong totalBytes)
    {
        ExitSaveProgress = overallFraction;
        ExitSaveStatusText = Loc.Format("Msg.ExitSavingDisk", mountPoint);
        ExitSaveDetailText = Loc.Format("Busy.ByteProgress",
            ByteFormatter.Format((ulong)(totalBytes * Math.Clamp(diskFraction, 0.0, 1.0))),
            ByteFormatter.Format(totalBytes));
    }

    /// <summary>
    /// Persists the application settings, including a profile for every mounted disk plus every
    /// auto-mount profile that failed to mount this session and hasn't been superseded since.
    /// </summary>
    internal void SaveSettings()
    {
        var current = _settingsStore.Load();
        var profiles = MergeProfiles(GetProfiles().ToList(), _unmountedProfiles);
        _unmountedProfiles.RemoveAll(p => !profiles.Contains(p));
        _settingsStore.Save(new()
        {
            RunAtStartup = StartupManager.IsEnabled,
            StartMinimized = current.StartMinimized,
            CloseToTray = current.CloseToTray,
            Language = LanguageManager.Instance.SavedLanguage,
            Theme = ThemeManager.Instance.SavedTheme,
            Disks = profiles,
            TempDirCompatWarningShown = current.TempDirCompatWarningShown,
            ContextMenuEnabled = current.ContextMenuEnabled,
            AutoCheckForUpdates = current.AutoCheckForUpdates,
            LastUpdateCheckUtc = current.LastUpdateCheckUtc,
            SkippedVersion = current.SkippedVersion,
            DefaultCompressionLevel = current.DefaultCompressionLevel,
            DefaultImageDirectory = current.DefaultImageDirectory,
        });
    }

    /// <summary>
    /// Shows a transient status-bar message naming the disk and file most recently read from
    /// or written to, reverting to <c>Status.Ready</c> after <see cref="DiskActivityStatusDuration"/>
    /// of inactivity. Driven by <see cref="DiskViewModel.ActivityObserved"/>.
    /// </summary>
    internal void ShowDiskActivityStatus(string mountPoint, bool isWrite, string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName))
        {
            fileName = filePath;
        }

        StatusText = Loc.Format(isWrite ? "Status.DiskWrite" : "Status.DiskRead", mountPoint, fileName);
        _diskActivityStatusTimer.Stop();
        _diskActivityStatusTimer.Start();
    }

    internal static DiskOptions ProfileToOptions(DiskProfile p) => new()
    {
        MountPoint = p.MountPoint,
        VolumeLabel = p.VolumeLabel,
        CapacityBytes = p.CapacityBytes,
        ReadOnly = p.ReadOnly,
        AutoMount = p.AutoMount,
        PersistImagePath = p.PersistImagePath,
        SourceArchivePath = p.SourceArchivePath,
        AutoSaveIntervalMinutes = p.AutoSaveIntervalMinutes,
        CompressionLevel = p.CompressionLevel,
        CustomZstdLevel = p.CustomZstdLevel,
        MaxSnapshotCount = p.MaxSnapshotCount,
        MaxSnapshotSizeBytes = p.MaxSnapshotSizeBytes,
        HighUsageWarnPercent = p.HighUsageWarnPercent,
        SaveImageOnExit = p.SaveImageOnExit,
    };

    private static void ResetTempIfPointingAt(string mountPoint)
    {
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        if (!string.IsNullOrEmpty(userTemp))
        {
            var expanded = Environment.ExpandEnvironmentVariables(userTemp);
            if (expanded.StartsWith(mountPoint, StringComparison.OrdinalIgnoreCase))
            {
                TempDirResetService.Reset();
            }
        }
    }

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

            if (!target.Disk.TryCloneFrom(vm.Disk, out var error))
            {
                _logger.LogWarning("Clone disk failed: {Source} -> {Target}: {Error}", vm.MountPoint, target.MountPoint, error);
                ShowWarning(error);
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
            BusyOverlay.Start(Loc.Get("Busy.ExportingImage"), totalBytes: vm.Disk.UsedBytes, cancellationSource: cts);
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

            vm.IsRemounting = true;

            if (vm.IsCurrentTempDir)
            {
                await Task.Run(TempDirResetService.Reset);
            }

            var currentPassword = vm.Disk.CurrentPassword;
            var oldMountPoint = old.MountPoint;
            await Task.Run(() => _mountManager.Unmount(oldMountPoint));

            try
            {
                var disk = await Task.Run(() =>
                {
                    var mounted = _mountManager.Mount(newOptions, currentPassword);
                    if (dialog.PasswordChanged)
                    {
                        mounted.SetPassword(dialog.Password);
                    }
                    return mounted;
                });

                vm.Dispose();
                Disks.Remove(vm);
                AddDiskSorted(new(disk));
                SaveSettings();
                StatusText = Loc.Format("Status.MountedWithCapacity", disk.MountPoint, newOptions.VolumeLabel, newOptions.CapacityBytes / (1024 * 1024));
                _logger.LogInformation("Edit disk remount succeeded: {OldMountPoint} -> {NewMountPoint}.", oldMountPoint, disk.MountPoint);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Edit disk remount failed: {OldMountPoint} -> {NewMountPoint}.", oldMountPoint, newOptions.MountPoint);
                vm.Dispose();
                Disks.Remove(vm);
                ShowError(Loc.Format("Msg.MountFailed", ex.Message));
                StatusText = Loc.Get("Status.MountFailed");
            }
        }
        else
        {
            var error = await Task.Run(() =>
            {
                if (!vm.Disk.TryApplyOptions(newOptions, out var applyError))
                {
                    return applyError;
                }

                if (dialog.PasswordChanged)
                {
                    vm.Disk.SetPassword(dialog.Password);
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

        if (confirm.ShowDialog() != true || !IsStillMounted(vm))
        {
            return;
        }

        _logger.LogInformation("Format disk confirmed for {MountPoint}.", vm.MountPoint);

        if (!vm.Disk.Format())
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
        BusyOverlay.Start(Loc.Get("Busy.ImportingArchive"), indeterminate: totalBytes == 0, totalBytes: totalBytes > 0 ? totalBytes : null, cancellationSource: cts);
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
        BusyOverlay.Start(Loc.Get("Busy.ImportingImage"), totalBytes: fileSizeBytes, cancellationSource: cts);
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

            if (!vm.Disk.TryApplyOptions(vm.Disk.Options with { PersistImagePath = dlg.FileName }, out var applyError))
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

    /// <summary>
    /// Writes a timestamped snapshot for <paramref name="vm"/> right now, via the same
    /// <see cref="RamDisk.SaveToImageWithSnapshot"/> call the regular "Save Image" action uses —
    /// snapshot creation always piggybacks on a full image save, there's no lighter-weight path.
    /// </summary>
    private async void ExecuteCreateSnapshotNow(DiskViewModel? vm)
    {
        if (vm is not { SnapshotsEnabled: true, HasImagePath: true })
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
        vm.IsSaving = true;
        using var cts = new CancellationTokenSource();
        BusyOverlay.Start(busyText, totalBytes: vm.Disk.UsedBytes, cancellationSource: cts);
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

    private void ExecuteSettings()
    {
        var config = _settingsStore.Load();
        var dialog = new SettingsDialog(config, UpdateCheckService) { Owner = Application.Current.MainWindow };

        if (dialog.ShowDialog() == true)
        {
            var updated = dialog.Result!;
            updated.Disks = config.Disks;
            _settingsStore.Save(updated);
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

                _settingsStore.Save(_settingsStore.Load() with { TempDirCompatWarningShown = true });
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
        vm.Dispose();
        Disks.Remove(vm);
        await Task.Run(() => _mountManager.Unmount(mountPoint));

        await DeleteDiskImageIfRequestedAsync(deleteImage, persistImagePath, sourceArchivePath);

        SaveSettings();
        StatusText = Loc.Format("Status.Unmounted", mountPoint);
        _logger.LogInformation("Unmount completed for {MountPoint}.", mountPoint);
    }

    /// <summary>
    /// Returns whether <paramref name="vm"/> is still in <see cref="Disks"/>. A modal dialog keeps
    /// pumping the dispatcher, so a CLI command or tray action can unmount the disk while one is
    /// open; a handler that acts on a disk after a dialog (or an await) must re-check, or it would
    /// operate on a disposed disk, or, for unmount/remount, on whatever disk has since been
    /// mounted at the same drive letter. Surfaces the reason via <see cref="StatusText"/>.
    /// </summary>
    /// <param name="vm">The disk the pending action targets.</param>
    /// <returns><c>true</c> if the disk is still mounted; otherwise <c>false</c>.</returns>
    private bool IsStillMounted(DiskViewModel vm)
    {
        if (Disks.Contains(vm))
        {
            return true;
        }

        _logger.LogInformation("Disk {MountPoint} was unmounted while a dialog was open; dropping the pending action.", vm.MountPoint);
        StatusText = Loc.Format("Status.DiskNoLongerMounted", vm.MountPoint);
        return false;
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
