using ManagedDrive.Cli.Core;
using static ManagedDrive.App.ViewModels.MainViewModelHelpers;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// Disk operations exposed to the <c>mdrive</c> CLI (<see cref="ICliDiskController"/>).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Formats the disk currently mounted at <paramref name="mountPoint"/> without any
    /// interactive confirmation, for use by the CLI command channel.
    /// </summary>
    /// <param name="mountPoint">The mount point to format (e.g. <c>"R:"</c>).</param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> if the disk is read-only; or
    /// <c>(false, string.Empty)</c> if no disk is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message)> FormatByMountPointAsync(string mountPoint)
    {
        _logger.LogInformation("CLI format requested for {MountPoint}.", mountPoint);

        var vm = FindDisk(mountPoint);
        if (vm == null)
        {
            return (false, string.Empty);
        }

        var formatted = await Task.Run(vm.Disk.Format);
        if (!IsStillMounted(vm))
        {
            return (false, Loc.Format("Status.DiskNoLongerMounted", mountPoint));
        }

        if (!formatted)
        {
            _logger.LogWarning("CLI format failed for {MountPoint}: disk is read-only.", mountPoint);
            return (false, Loc.Get("Msg.FormatDiskReadOnly"));
        }

        vm.Refresh();
        StatusText = Loc.Format("Status.FormatDisk", mountPoint);
        _logger.LogInformation("CLI format completed for {MountPoint}.", mountPoint);
        return (true, StatusText);
    }

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
            TryOpenInExplorer(disk.MountPoint);
            return (true, StatusText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI mount-archive failed for {ArchivePath}.", archivePath);
            return (false, Loc.Format("Msg.MountFailed", ex.Message));
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

        var vm = FindDisk(mountPoint);
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
    public async Task<(bool Success, string Message)> CloneByMountPointAsync(string sourceMountPoint, string targetMountPoint)
    {
        _logger.LogInformation("CLI clone requested: {Source} -> {Target}.", sourceMountPoint, targetMountPoint);

        var source = FindDisk(sourceMountPoint);
        if (source == null)
        {
            return (false, Loc.Format("Msg.CliMountPointNotMounted", sourceMountPoint));
        }

        var target = FindDisk(targetMountPoint);
        if (target == null)
        {
            return (false, Loc.Format("Msg.CliMountPointNotMounted", targetMountPoint));
        }

        if (target == source)
        {
            return (false, Loc.Get("Val.CliCloneTargetIsSource"));
        }

        if (target.IsReadOnly)
        {
            return (false, Loc.Format("Val.CliCloneTargetReadOnly", targetMountPoint));
        }

        // Off the UI thread: copying a large disk's content would otherwise freeze the window.
        var (cloned, error) = await Task.Run(() =>
        {
            var ok = target.Disk.TryCloneFrom(source.Disk, out var cloneError);
            return (ok, cloneError);
        });

        if (!cloned)
        {
            _logger.LogWarning("CLI clone failed: {Source} -> {Target}: {Error}", sourceMountPoint, targetMountPoint, error);
            return (false, error ?? string.Empty);
        }

        // The target may have been unmounted while the clone ran off the UI thread.
        if (!IsStillMounted(target))
        {
            return (false, Loc.Format("Msg.CliMountPointNotMounted", targetMountPoint));
        }

        target.Refresh();
        _logger.LogInformation("CLI clone completed: {Source} -> {Target}.", sourceMountPoint, targetMountPoint);
        return (true, Loc.Format("Status.DiskCloned", sourceMountPoint, targetMountPoint));
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

        var vm = FindDisk(mountPoint);
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
    /// <c>(true, message)</c> on success; <c>(false, error)</c> if the password could not be set;
    /// or <c>(false, string.Empty)</c> if no disk is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message)> SetPasswordByMountPointAsync(string mountPoint, string? newPassword)
    {
        _logger.LogInformation("CLI set-password requested for {MountPoint}.", mountPoint);

        var vm = FindDisk(mountPoint);
        if (vm == null)
        {
            return (false, string.Empty);
        }

        if (await Task.Run(() => TrySetPassword(vm.Disk, newPassword)) is { } error)
        {
            return (false, error);
        }

        _logger.LogInformation("CLI set-password completed for {MountPoint}.", mountPoint);
        return (true, newPassword is null
            ? Loc.Format("Status.PasswordRemoved", mountPoint)
            : Loc.Format("Status.PasswordSet", mountPoint));
    }

    /// <summary>
    /// Opens <paramref name="path"/> in Explorer, logging instead of throwing when that fails so a
    /// cosmetic launch problem is never reported as a failed operation.
    /// </summary>
    /// <param name="path">The folder to open.</param>
    private void TryOpenInExplorer(string path)
    {
        try
        {
            Process.Start("explorer.exe", path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not open {Path} in Explorer.", path);
        }
    }

    /// <summary>
    /// Calls <see cref="RamDisk.SetPassword"/>, turning the expected failure (plaintext snapshots
    /// that could not be deleted before encrypting) into a user-facing message instead of an
    /// exception. Safe to call off the UI thread.
    /// </summary>
    /// <param name="disk">The disk whose password is changed.</param>
    /// <param name="newPassword">The new password, or <see langword="null"/> to remove protection.</param>
    /// <returns>The localized error message, or <see langword="null"/> on success.</returns>
    private string? TrySetPassword(RamDisk disk, string? newPassword)
    {
        try
        {
            disk.SetPassword(newPassword);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Setting the password of {MountPoint} failed.", disk.Options.MountPoint);
            return Loc.Format("Msg.SetPasswordFailed", ex.Message);
        }
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

        var vm = FindDisk(mountPoint);
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

        var vm = FindDisk(mountPoint);
        if (vm == null)
        {
            return (false, string.Empty);
        }

        // Same destination rules as the Clone Disk dialog: never overwrite a live disk's own image
        // (its auto-save may be writing it) or write onto a RAM disk.
        var otherDisks = GetOtherDiskOptions(excluding: null);
        if (CloneDiskDialog.GetExportPathError(outputPath, otherDisks, [.. otherDisks.Select(d => d.MountPoint)]) is { } errorKey)
        {
            _logger.LogWarning("CLI export rejected: {MountPoint} -> {OutputPath} ({Reason}).", mountPoint, outputPath, errorKey);
            return (false, Loc.Get(errorKey));
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

        var vm = FindDisk(mountPoint);
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

        var saveError = await UnmountDiskAsync(vm);

        await DeleteDiskImageIfRequestedAsync(deleteImage, persistImagePath, sourceArchivePath);

        SaveSettings();
        ShowUnmountResult(mountPoint, saveError);
        return true;
    }
}
