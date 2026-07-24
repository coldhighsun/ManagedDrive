namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// The mode the create-disk dialog is operating in. Only affects how capacity/label are
/// sourced and validated; UI prefill is handled by the dialog itself.
/// </summary>
public enum CreateDiskMode
{
    /// <summary>
    /// Creating a brand-new disk.
    /// </summary>
    Create,

    /// <summary>
    /// Editing an existing, non-archive disk. Validated identically to <see cref="Create"/>.
    /// </summary>
    Edit,

    /// <summary>
    /// Importing an existing <c>.mdr</c> image; capacity and label come from the image.
    /// </summary>
    ImportImage,

    /// <summary>
    /// Importing (or editing) an archive-sourced disk; capacity and label come from the archive
    /// and the disk is forced read-only.
    /// </summary>
    ImportArchive,
}

/// <summary>
/// Distinct validation failures the create-disk builder can report. The presentation layer maps
/// each value to a localized message, keeping this pure-logic class free of any UI dependency.
/// </summary>
public enum CreateDiskValidationError
{
    /// <summary>No error; validation succeeded.</summary>
    None,

    /// <summary>No drive letter was selected.</summary>
    NoDriveLetter,

    /// <summary>Capacity was out of the allowed range.</summary>
    BadCapacity,

    /// <summary>The image path was not a valid, rooted path with an existing parent directory.</summary>
    BadImagePath,

    /// <summary>The image path names a snapshot file.</summary>
    ImagePathIsSnapshot,

    /// <summary>The image path is already used by another disk.</summary>
    ImagePathInUse,

    /// <summary>The image path lives under a mounted RAM disk.</summary>
    ImagePathOnRamDisk,

    /// <summary>A read-only disk was requested without an image path.</summary>
    ReadOnlyRequiresImage,

    /// <summary>A read-only disk's image file does not exist.</summary>
    ReadOnlyImageNotFound,

    /// <summary>Auto-save was enabled without an image path.</summary>
    AutoSaveNoImage,

    /// <summary>The auto-save interval was out of range.</summary>
    BadAutoSaveInterval,

    /// <summary>The snapshot count was out of range.</summary>
    BadSnapshotCount,

    /// <summary>The snapshot size was out of range.</summary>
    BadSnapshotSize,

    /// <summary>The high-usage warning percentage was out of range.</summary>
    BadHighUsagePercent,

    /// <summary>Encryption was enabled but no password was entered.</summary>
    PasswordRequired,

    /// <summary>The two password fields did not match.</summary>
    PasswordMismatch,

    /// <summary>The password was shorter than the minimum length.</summary>
    PasswordTooShort,

    /// <summary>The password was longer than the maximum length.</summary>
    PasswordTooLong,
}

/// <summary>
/// Plain, WPF-free snapshot of the create-disk dialog's inputs. The dialog reads its controls
/// once and populates this record; <see cref="CreateDiskOptionsBuilder"/> validates and builds
/// <see cref="DiskOptions"/> from it.
/// </summary>
public sealed record CreateDiskInput
{
    /// <summary>The selected mount point (e.g. <c>"Z:"</c>), or <c>null</c> if none was chosen.</summary>
    public string? MountPoint { get; init; }

    /// <summary>The dialog mode.</summary>
    public CreateDiskMode Mode { get; init; }

    /// <summary>Capacity/label sourced from the image or archive, for import modes.</summary>
    public ulong ImportCapacityBytes { get; init; }

    /// <summary>Volume label sourced from the image or archive, for import modes.</summary>
    public string ImportVolumeLabel { get; init; } = string.Empty;

    /// <summary>Archive path, for archive-import mode.</summary>
    public string ImportArchivePath { get; init; } = string.Empty;

    /// <summary>Capacity display value (in <see cref="CapacityIsGb"/> units).</summary>
    public int CapacityValue { get; init; }

    /// <summary>Whether <see cref="CapacityValue"/> is in GB (<c>true</c>) or MB (<c>false</c>).</summary>
    public bool CapacityIsGb { get; init; }

    /// <summary>The maximum allowed capacity display value for the selected unit.</summary>
    public int MaxCapacityValue { get; init; }

    /// <summary>The entered volume label (used in non-import modes).</summary>
    public string VolumeLabel { get; init; } = string.Empty;

    /// <summary>The raw image-path text, or <c>null</c>/blank when none.</summary>
    public string? ImagePathText { get; init; }

    /// <summary>Whether the disk should be read-only.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>Whether the disk should auto-mount on startup.</summary>
    public bool AutoMount { get; init; }

    /// <summary>Whether periodic auto-save is enabled.</summary>
    public bool AutoSaveEnabled { get; init; }

    /// <summary>The auto-save interval, in minutes.</summary>
    public int IntervalValue { get; init; }

    /// <summary>Whether snapshot count pruning is enabled.</summary>
    public bool SnapshotCountEnabled { get; init; }

    /// <summary>The maximum snapshot count.</summary>
    public int SnapshotCountValue { get; init; }

    /// <summary>Whether snapshot size pruning is enabled.</summary>
    public bool SnapshotSizeEnabled { get; init; }

    /// <summary>The maximum snapshot size display value.</summary>
    public int SnapshotSizeValue { get; init; }

    /// <summary>Whether <see cref="SnapshotSizeValue"/> is in GB (<c>true</c>) or MB (<c>false</c>).</summary>
    public bool SnapshotSizeIsGb { get; init; }

    /// <summary>Whether the high-usage warning is enabled.</summary>
    public bool HighUsageWarnEnabled { get; init; }

    /// <summary>The high-usage warning percentage.</summary>
    public int HighUsageWarnPercentValue { get; init; }

    /// <summary>The selected compression level.</summary>
    public ImageCompressionLevel CompressionLevel { get; init; } = ImageCompressionLevel.Fastest;

    /// <summary>Whether to save the image on exit.</summary>
    public bool SaveImageOnExit { get; init; }

    /// <summary>Whether the encrypt-image checkbox is checked.</summary>
    public bool EncryptChecked { get; init; }

    /// <summary>The first password field.</summary>
    public string Password1 { get; init; } = string.Empty;

    /// <summary>The second (confirm) password field.</summary>
    public string Password2 { get; init; } = string.Empty;

    /// <summary>Whether the disk was already encrypted when the dialog opened (edit mode).</summary>
    public bool WasEncrypted { get; init; }

    /// <summary>The disk's original password, for detecting "kept unchanged" in edit mode.</summary>
    public string? OriginalPassword { get; init; }

    /// <summary>Options of all other active disks, used for path-collision checks.</summary>
    public IReadOnlyList<DiskOptions> OtherDisks { get; init; } = [];
}

/// <summary>
/// The outcome of <see cref="CreateDiskOptionsBuilder.Build"/>: either a built
/// <see cref="DiskOptions"/> plus the resolved password state, or a validation error.
/// </summary>
public sealed record CreateDiskBuildResult
{
    /// <summary>The built options, or <c>null</c> when <see cref="Error"/> is not <see cref="CreateDiskValidationError.None"/>.</summary>
    public DiskOptions? Options { get; init; }

    /// <summary>The validation error, or <see cref="CreateDiskValidationError.None"/> on success.</summary>
    public CreateDiskValidationError Error { get; init; }

    /// <summary>
    /// The resolved plaintext password when <see cref="PasswordChanged"/> is <c>true</c> and this
    /// is non-null (set/change); <c>null</c> with <see cref="PasswordChanged"/> <c>true</c> means
    /// "remove protection".
    /// </summary>
    public string? Password { get; init; }

    /// <summary>Whether the password input requires a change (set, changed, or removed).</summary>
    public bool PasswordChanged { get; init; }

    /// <summary><c>true</c> when <see cref="Error"/> is <see cref="CreateDiskValidationError.None"/>.</summary>
    public bool Success => Error == CreateDiskValidationError.None;
}

/// <summary>
/// Pure-logic validator and builder for <see cref="DiskOptions"/> from create-disk dialog input.
/// Extracted from the WPF dialog so the validation can be unit-tested and reused. Contains no UI
/// or localization dependency; failures are reported as <see cref="CreateDiskValidationError"/>
/// codes that the presentation layer maps to messages.
/// </summary>
public static class CreateDiskOptionsBuilder
{
    /// <summary>The minimum accepted password length.</summary>
    public const int MinPasswordLength = 8;

    /// <summary>The maximum accepted password length.</summary>
    public const int MaxPasswordLength = 64;

    /// <summary>
    /// Validates <paramref name="input"/> and builds the corresponding <see cref="DiskOptions"/>.
    /// </summary>
    /// <param name="input">The dialog input snapshot.</param>
    /// <returns>
    /// A <see cref="CreateDiskBuildResult"/> carrying either the built options or a validation error.
    /// </returns>
    public static CreateDiskBuildResult Build(CreateDiskInput input)
    {
        if (string.IsNullOrEmpty(input.MountPoint))
        {
            return Fail(CreateDiskValidationError.NoDriveLetter);
        }

        var mountPoint = input.MountPoint;

        if (input.Mode == CreateDiskMode.ImportArchive)
        {
            return BuildArchiveImportOptions(input, mountPoint);
        }

        var isImport = input.Mode == CreateDiskMode.ImportImage;

        ulong capacityBytes;
        if (isImport)
        {
            capacityBytes = input.ImportCapacityBytes;
        }
        else
        {
            if (input.CapacityValue <= 0 || input.CapacityValue > input.MaxCapacityValue)
            {
                return Fail(CreateDiskValidationError.BadCapacity);
            }

            capacityBytes = ByteUnitConverter.ToBytes(input.CapacityValue, input.CapacityIsGb);
        }

        var imagePath = string.IsNullOrWhiteSpace(input.ImagePathText)
            ? null
            : input.ImagePathText.Trim();

        if (imagePath != null)
        {
            if (!IsValidImagePath(imagePath))
            {
                return Fail(CreateDiskValidationError.BadImagePath);
            }

            var availability = ValidateImagePathAvailable(imagePath, input.OtherDisks);
            if (availability != CreateDiskValidationError.None)
            {
                return Fail(availability);
            }

            // Depends on the currently selected drive letter (which may change after the image
            // file is picked), so this check stays here rather than at selection time.
            var allMountPoints = input.OtherDisks.Select(d => d.MountPoint).Append(mountPoint);
            if (allMountPoints.Any(mp => imagePath.StartsWith(mp, StringComparison.OrdinalIgnoreCase)))
            {
                return Fail(CreateDiskValidationError.ImagePathOnRamDisk);
            }
        }

        var isReadOnly = input.IsReadOnly;

        if (isReadOnly)
        {
            if (imagePath == null)
            {
                return Fail(CreateDiskValidationError.ReadOnlyRequiresImage);
            }

            if (!File.Exists(imagePath))
            {
                return Fail(CreateDiskValidationError.ReadOnlyImageNotFound);
            }
        }

        uint? autoSaveIntervalMinutes = null;
        if (input.AutoSaveEnabled && !isReadOnly)
        {
            if (imagePath == null)
            {
                return Fail(CreateDiskValidationError.AutoSaveNoImage);
            }

            if (input.IntervalValue < 1 || input.IntervalValue > 60)
            {
                return Fail(CreateDiskValidationError.BadAutoSaveInterval);
            }

            autoSaveIntervalMinutes = (uint)input.IntervalValue;
        }

        uint? maxSnapshotCount = null;
        ulong? maxSnapshotSizeBytes = null;
        if (autoSaveIntervalMinutes is not null)
        {
            if (input.SnapshotCountEnabled)
            {
                if (input.SnapshotCountValue < 1 || input.SnapshotCountValue > 20)
                {
                    return Fail(CreateDiskValidationError.BadSnapshotCount);
                }

                maxSnapshotCount = (uint)input.SnapshotCountValue;
            }

            if (input.SnapshotSizeEnabled)
            {
                if (input.SnapshotSizeValue < 1)
                {
                    return Fail(CreateDiskValidationError.BadSnapshotSize);
                }

                maxSnapshotSizeBytes = ByteUnitConverter.ToBytes(input.SnapshotSizeValue, input.SnapshotSizeIsGb);
            }
        }

        if (!TryResolveHighUsagePercent(input, out var highUsageWarnPercent))
        {
            return Fail(CreateDiskValidationError.BadHighUsagePercent);
        }

        var passwordResult = ResolvePassword(input);
        if (!passwordResult.Success)
        {
            return passwordResult;
        }

        var options = new DiskOptions
        {
            MountPoint = mountPoint,
            VolumeLabel = isImport ? input.ImportVolumeLabel : input.VolumeLabel.Trim(),
            CapacityBytes = capacityBytes,
            ReadOnly = isReadOnly,
            AutoMount = input.AutoMount,
            PersistImagePath = imagePath,
            AutoSaveIntervalMinutes = autoSaveIntervalMinutes,
            CompressionLevel = input.CompressionLevel,
            MaxSnapshotCount = maxSnapshotCount,
            MaxSnapshotSizeBytes = maxSnapshotSizeBytes,
            HighUsageWarnPercent = highUsageWarnPercent,
            SaveImageOnExit = input.SaveImageOnExit,
        };

        return new()
        {
            Options = options,
            Password = passwordResult.Password,
            PasswordChanged = passwordResult.PasswordChanged,
        };
    }

    /// <summary>
    /// Validates that <paramref name="imagePath"/> is not a snapshot file and is not already used
    /// as another active disk's persistence target. Runs both at image-selection time and at build
    /// time, so it is exposed independently.
    /// </summary>
    /// <param name="imagePath">The image file path to validate.</param>
    /// <param name="otherDisks">Options of all other active disks.</param>
    /// <returns>
    /// <see cref="CreateDiskValidationError.None"/> when available; otherwise the specific error.
    /// </returns>
    public static CreateDiskValidationError ValidateImagePathAvailable(string imagePath, IReadOnlyList<DiskOptions> otherDisks)
    {
        if (SnapshotManager.IsSnapshotFileName(Path.GetFileName(imagePath)))
        {
            return CreateDiskValidationError.ImagePathIsSnapshot;
        }

        if (otherDisks.Any(d => d.PersistImagePath != null &&
            string.Equals(d.PersistImagePath, imagePath, StringComparison.OrdinalIgnoreCase)))
        {
            return CreateDiskValidationError.ImagePathInUse;
        }

        return CreateDiskValidationError.None;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> is a rooted path whose parent directory
    /// exists and whose full path can be resolved.
    /// </summary>
    /// <param name="path">The path to validate.</param>
    /// <returns>
    /// <c>true</c> when the path is a usable image target.
    /// </returns>
    public static bool IsValidImagePath(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            _ = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static CreateDiskBuildResult BuildArchiveImportOptions(CreateDiskInput input, string mountPoint)
    {
        if (!TryResolveHighUsagePercent(input, out var highUsageWarnPercent))
        {
            return Fail(CreateDiskValidationError.BadHighUsagePercent);
        }

        var options = new DiskOptions
        {
            MountPoint = mountPoint,
            VolumeLabel = input.ImportVolumeLabel,
            CapacityBytes = input.ImportCapacityBytes,
            ReadOnly = true,
            AutoMount = input.AutoMount,
            SourceArchivePath = input.ImportArchivePath,
            HighUsageWarnPercent = highUsageWarnPercent,
        };

        return new() { Options = options };
    }

    private static bool TryResolveHighUsagePercent(CreateDiskInput input, out double? highUsageWarnPercent)
    {
        highUsageWarnPercent = null;
        if (!input.HighUsageWarnEnabled)
        {
            return true;
        }

        if (input.HighUsageWarnPercentValue < 1 || input.HighUsageWarnPercentValue > 99)
        {
            return false;
        }

        highUsageWarnPercent = input.HighUsageWarnPercentValue;
        return true;
    }

    private static CreateDiskBuildResult ResolvePassword(CreateDiskInput input)
    {
        if (!input.EncryptChecked)
        {
            // Explicitly unchecked while editing an already-encrypted disk means "remove
            // password protection"; otherwise there is simply nothing to change.
            return new() { PasswordChanged = input.WasEncrypted };
        }

        var password1 = input.Password1;
        var password2 = input.Password2;

        if (string.IsNullOrEmpty(password1) && string.IsNullOrEmpty(password2))
        {
            return Fail(CreateDiskValidationError.PasswordRequired);
        }

        if (password1 != password2)
        {
            return Fail(CreateDiskValidationError.PasswordMismatch);
        }

        if (input.WasEncrypted && password1 == input.OriginalPassword)
        {
            // Editing an already-encrypted disk with the fields left as-is: keep the current
            // password unchanged.
            return new() { PasswordChanged = false };
        }

        if (password1.Length < MinPasswordLength)
        {
            return Fail(CreateDiskValidationError.PasswordTooShort);
        }

        if (password1.Length > MaxPasswordLength)
        {
            return Fail(CreateDiskValidationError.PasswordTooLong);
        }

        return new() { Password = password1, PasswordChanged = true };
    }

    private static CreateDiskBuildResult Fail(CreateDiskValidationError error) => new() { Error = error };
}
