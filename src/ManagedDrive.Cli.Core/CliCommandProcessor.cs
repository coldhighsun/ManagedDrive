using System.CommandLine;

namespace ManagedDrive.Cli.Core;

/// <summary>
/// Parses and dispatches ManagedDrive's CLI subcommands (<c>mount</c>, <c>unmount</c>,
/// <c>list</c>, <c>snapshot</c>, <c>exit</c>) using <c>System.CommandLine</c>. Returns a structured
/// <see cref="CliOutcome"/> rather than rendered text — terminal rendering is the caller's
/// concern (see <c>ManagedDrive.Cli</c>'s renderer).
/// </summary>
public static class CliCommandProcessor
{
    /// <summary>
    /// Parses <paramref name="args"/> and executes the matching subcommand against
    /// <paramref name="diskController"/>.
    /// </summary>
    public static async Task<CliOutcome> ExecuteAsync(string[] args, ICliDiskController diskController)
    {
        var buffer = new StringWriter();
        CliOutcome? outcome = null;

        var mountImageArgument = new Argument<string>("image-path")
        {
            Description = "Path to an existing .mdr disk image.",
        };
        var mountDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter to mount at (e.g. R:), or the path of an existing empty directory.",
        };
        var mountReadOnlyOption = new Option<bool?>("--read-only")
        {
            Description = "Mount as read-only. If omitted, keeps the saved profile's value (or the default: writable).",
        };
        var mountAutoMountOption = new Option<bool?>("--auto-mount")
        {
            Description = "Re-mount this disk automatically on next app startup. If omitted, keeps the saved profile's value (or the default: off).",
        };
        var mountAutoSaveMinutesOption = new Option<uint?>("--auto-save-minutes")
        {
            Description = "Auto-save interval in minutes. If omitted, keeps the saved profile's value (or the default: disabled).",
        };
        var mountCompressionOption = new Option<ImageCompressionLevel?>("--compression")
        {
            Description = "Image compression level: None, Fastest, Optimal, or SmallestSize. If omitted, keeps the saved profile's value (or the default: Fastest).",
        };
        var mountCustomZstdLevelOption = new Option<int?>("--custom-zstd-level")
        {
            Description = "Custom Zstd compression level (1-22), overriding the preset mapping for --compression. Only takes effect when the compression level is not None.",
        };
        var mountMaxSnapshotCountOption = new Option<uint?>("--max-snapshot-count")
        {
            Description = "Maximum number of retained snapshots. If omitted, keeps the saved profile's value (or the default: unlimited).",
        };
        var mountMaxSnapshotSizeMbOption = new Option<uint?>("--max-snapshot-size-mb")
        {
            Description = "Maximum total size, in MB, of retained snapshots. If omitted, keeps the saved profile's value (or the default: unlimited).",
        };
        var mountHighUsageWarnPercentOption = new Option<double?>("--high-usage-warn-percent")
        {
            Description = "Usage percentage (0-100) at which a high-usage warning is raised. If omitted, keeps the saved profile's value (or the default: 90).",
        };
        var mountPasswordOption = new Option<string?>("--password")
        {
            Description = "Password to unlock the image, if it is encrypted. Prefer --password-file to avoid the password appearing in shell history or the process list.",
        };
        var mountPasswordFileOption = new Option<string?>("--password-file")
        {
            Description = "Path to a file whose first line is the password to unlock the image, if it is encrypted. Mutually exclusive with --password.",
        };

        var mountCommand = new Command("mount", "Mounts an existing .mdr disk image at a drive letter.");
        mountCommand.Arguments.Add(mountImageArgument);
        mountCommand.Arguments.Add(mountDriveArgument);
        mountCommand.Options.Add(mountReadOnlyOption);
        mountCommand.Options.Add(mountAutoMountOption);
        mountCommand.Options.Add(mountAutoSaveMinutesOption);
        mountCommand.Options.Add(mountCompressionOption);
        mountCommand.Options.Add(mountCustomZstdLevelOption);
        mountCommand.Options.Add(mountMaxSnapshotCountOption);
        mountCommand.Options.Add(mountMaxSnapshotSizeMbOption);
        mountCommand.Options.Add(mountHighUsageWarnPercentOption);
        mountCommand.Options.Add(mountPasswordOption);
        mountCommand.Options.Add(mountPasswordFileOption);
        mountCommand.SetAction(async (parseResult, _) =>
        {
            var maxSnapshotSizeMb = parseResult.GetValue(mountMaxSnapshotSizeMbOption);
            var customZstdLevel = parseResult.GetValue(mountCustomZstdLevelOption);
            var password = parseResult.GetValue(mountPasswordOption);
            var passwordFile = parseResult.GetValue(mountPasswordFileOption);

            if (customZstdLevel is < 1 or > 22)
            {
                outcome = new CliOutcome(false, "--custom-zstd-level must be between 1 and 22.", null, 1);
                return 1;
            }

            if (password is not null && passwordFile is not null)
            {
                outcome = new CliOutcome(false, "--password and --password-file cannot both be specified.", null, 1);
                return 1;
            }

            if (passwordFile is not null && !TryReadPasswordFile(passwordFile, out password, out var readError))
            {
                outcome = new CliOutcome(false, readError!, null, 1);
                return 1;
            }

            var overrides = new CliMountOverrides
            {
                ReadOnly = parseResult.GetValue(mountReadOnlyOption),
                AutoMount = parseResult.GetValue(mountAutoMountOption),
                AutoSaveIntervalMinutes = parseResult.GetValue(mountAutoSaveMinutesOption),
                CompressionLevel = parseResult.GetValue(mountCompressionOption),
                CustomZstdLevel = customZstdLevel,
                MaxSnapshotCount = parseResult.GetValue(mountMaxSnapshotCountOption),
                MaxSnapshotSizeBytes = maxSnapshotSizeMb * 1024UL * 1024UL,
                HighUsageWarnPercent = parseResult.GetValue(mountHighUsageWarnPercentOption),
                Password = password,
            };

            var exitCode = await MountAsync(
                parseResult.GetValue(mountImageArgument)!,
                parseResult.GetValue(mountDriveArgument)!,
                overrides,
                diskController,
                o => outcome = o);
            return exitCode;
        });

        var mountArchiveArgument = new Argument<string>("archive-path")
        {
            Description = "Path to an archive file (zip, 7z, rar, tar, ...) to mount as a read-only disk.",
        };
        var mountArchiveDriveArgument = new Argument<string?>("drive-letter")
        {
            Description = "Drive letter to mount at (e.g. R:), or the path of an existing empty directory. If omitted, the first free letter from Z: down to D: is picked automatically.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var mountArchiveAutoMountOption = new Option<bool?>("--auto-mount")
        {
            Description = "Re-mount this disk automatically on next app startup. If omitted, keeps the saved profile's value (or the default: off).",
        };

        var mountArchiveCommand = new Command("mount-archive", "Mounts the contents of an archive file as a new read-only disk.");
        mountArchiveCommand.Arguments.Add(mountArchiveArgument);
        mountArchiveCommand.Arguments.Add(mountArchiveDriveArgument);
        mountArchiveCommand.Options.Add(mountArchiveAutoMountOption);
        mountArchiveCommand.SetAction(async (parseResult, _) =>
        {
            var overrides = new CliMountOverrides
            {
                AutoMount = parseResult.GetValue(mountArchiveAutoMountOption),
            };

            var exitCode = await MountArchiveAsync(
                parseResult.GetValue(mountArchiveArgument)!,
                parseResult.GetValue(mountArchiveDriveArgument),
                overrides,
                diskController,
                o => outcome = o);
            return exitCode;
        });

        var unmountDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter of a currently mounted disk, e.g. R:",
        };
        var unmountDeleteImageOption = new Option<bool>("--delete-image")
        {
            Description = "Also delete the disk's backing image file (and any snapshots) after unmounting.",
        };
        var unmountCommand = new Command("unmount", "Unmounts a mounted disk by drive letter.");
        unmountCommand.Arguments.Add(unmountDriveArgument);
        unmountCommand.Options.Add(unmountDeleteImageOption);
        unmountCommand.SetAction(async (parseResult, _) =>
            await UnmountAsync(
                parseResult.GetValue(unmountDriveArgument)!,
                parseResult.GetValue(unmountDeleteImageOption),
                diskController,
                o => outcome = o));

        var formatDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter of a currently mounted disk, e.g. R:",
        };
        var formatYesOption = new Option<bool>("--yes", "-y")
        {
            Description = "Confirm the destructive format operation.",
        };
        var formatCommand = new Command("format", "Formats a mounted disk, permanently deleting all files on it.");
        formatCommand.Arguments.Add(formatDriveArgument);
        formatCommand.Options.Add(formatYesOption);
        formatCommand.SetAction(async (parseResult, _) =>
            await FormatAsync(
                parseResult.GetValue(formatDriveArgument)!,
                parseResult.GetValue(formatYesOption),
                diskController,
                o => outcome = o));

        var saveDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter of a currently mounted disk, e.g. R:",
        };
        var saveCommand = new Command("save", "Saves a mounted disk's contents to its backing .mdr image immediately.");
        saveCommand.Arguments.Add(saveDriveArgument);
        saveCommand.SetAction(async (parseResult, _) =>
            await SaveAsync(parseResult.GetValue(saveDriveArgument)!, diskController, o => outcome = o));

        var setPasswordDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter of a currently mounted disk, e.g. R:",
        };
        var setPasswordOption = new Option<string?>("--password")
        {
            Description = "The new password. Prefer --password-file to avoid it appearing in shell history or the process list.",
        };
        var setPasswordFileOption = new Option<string?>("--password-file")
        {
            Description = "Path to a file whose first line is the new password. Mutually exclusive with --password.",
        };
        var setPasswordRemoveOption = new Option<bool>("--remove")
        {
            Description = "Remove password protection instead of setting a new password.",
        };
        var setPasswordCommand = new Command("set-password", "Sets or removes the encryption password of a mounted disk. Takes effect on the next save.");
        setPasswordCommand.Arguments.Add(setPasswordDriveArgument);
        setPasswordCommand.Options.Add(setPasswordOption);
        setPasswordCommand.Options.Add(setPasswordFileOption);
        setPasswordCommand.Options.Add(setPasswordRemoveOption);
        setPasswordCommand.SetAction(async (parseResult, _) =>
        {
            var password = parseResult.GetValue(setPasswordOption);
            var passwordFile = parseResult.GetValue(setPasswordFileOption);
            var remove = parseResult.GetValue(setPasswordRemoveOption);

            var specifiedCount = (password is not null ? 1 : 0) + (passwordFile is not null ? 1 : 0) + (remove ? 1 : 0);
            if (specifiedCount != 1)
            {
                outcome = new CliOutcome(false, "Specify exactly one of --password, --password-file, or --remove.", null, 1);
                return 1;
            }

            if (passwordFile is not null && !TryReadPasswordFile(passwordFile, out password, out var readError))
            {
                outcome = new CliOutcome(false, readError!, null, 1);
                return 1;
            }

            var exitCode = await SetPasswordAsync(
                parseResult.GetValue(setPasswordDriveArgument)!,
                remove ? null : password,
                diskController,
                o => outcome = o);
            return exitCode;
        });

        var createDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter to mount the new disk at (e.g. R:), or the path of an existing empty directory.",
        };
        var createCapacityMbOption = new Option<uint>("--capacity-mb")
        {
            Description = "The disk's capacity in MB.",
            Required = true,
        };
        var createLabelOption = new Option<string?>("--label")
        {
            Description = "The volume label. Defaults to \"RAM Disk\" if omitted.",
        };
        var createImageOption = new Option<string?>("--image")
        {
            Description = "Path to persist the disk to (created on first save). Omit for a memory-only disk discarded on unmount.",
        };
        var createPasswordOption = new Option<string?>("--password")
        {
            Description = "Password to encrypt --image with. Requires --image. Prefer --password-file to avoid it appearing in shell history or the process list.",
        };
        var createPasswordFileOption = new Option<string?>("--password-file")
        {
            Description = "Path to a file whose first line is the password to encrypt --image with. Mutually exclusive with --password.",
        };

        var createCommand = new Command("create", "Creates a brand-new, empty RAM disk.");
        createCommand.Arguments.Add(createDriveArgument);
        createCommand.Options.Add(createCapacityMbOption);
        createCommand.Options.Add(createLabelOption);
        createCommand.Options.Add(createImageOption);
        createCommand.Options.Add(createPasswordOption);
        createCommand.Options.Add(createPasswordFileOption);
        createCommand.SetAction(async (parseResult, _) =>
        {
            var password = parseResult.GetValue(createPasswordOption);
            var passwordFile = parseResult.GetValue(createPasswordFileOption);

            if (password is not null && passwordFile is not null)
            {
                outcome = new CliOutcome(false, "--password and --password-file cannot both be specified.", null, 1);
                return 1;
            }

            if (passwordFile is not null && !TryReadPasswordFile(passwordFile, out password, out var readError))
            {
                outcome = new CliOutcome(false, readError!, null, 1);
                return 1;
            }

            var imagePath = parseResult.GetValue(createImageOption);
            if (password is not null && imagePath is null)
            {
                outcome = new CliOutcome(false, "--password/--password-file requires --image.", null, 1);
                return 1;
            }

            var exitCode = await CreateAsync(
                parseResult.GetValue(createDriveArgument)!,
                parseResult.GetValue(createCapacityMbOption) * 1024UL * 1024UL,
                parseResult.GetValue(createLabelOption),
                imagePath,
                password,
                diskController,
                o => outcome = o);
            return exitCode;
        });

        var lsDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter of a currently mounted disk, e.g. R:",
        };
        var lsPathArgument = new Argument<string?>("path")
        {
            Description = "Directory to list, e.g. \\Folder. Omit to list the root.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var lsCommand = new Command("ls", "Lists the immediate children of a directory on a mounted disk.");
        lsCommand.Arguments.Add(lsDriveArgument);
        lsCommand.Arguments.Add(lsPathArgument);
        lsCommand.SetAction(async (parseResult, _) =>
            await LsAsync(
                parseResult.GetValue(lsDriveArgument)!,
                parseResult.GetValue(lsPathArgument),
                diskController,
                o => outcome = o));

        var snapshotCreateDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter of a currently mounted disk, e.g. R:",
        };
        var snapshotCreateCommand = new Command("create", "Writes a timestamped snapshot of a mounted disk right now.");
        snapshotCreateCommand.Arguments.Add(snapshotCreateDriveArgument);
        snapshotCreateCommand.SetAction(async (parseResult, _) =>
            await SnapshotCreateAsync(parseResult.GetValue(snapshotCreateDriveArgument)!, diskController, o => outcome = o));

        var snapshotListDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter of a currently mounted disk, e.g. R:",
        };
        var snapshotListCommand = new Command("list", "Lists available snapshots of a mounted disk, newest first.");
        snapshotListCommand.Arguments.Add(snapshotListDriveArgument);
        snapshotListCommand.SetAction(async (parseResult, _) =>
            await SnapshotListAsync(parseResult.GetValue(snapshotListDriveArgument)!, diskController, o => outcome = o));

        var snapshotRestoreDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter of a currently mounted disk, e.g. R:",
        };
        var snapshotRestoreIndexArgument = new Argument<int>("index")
        {
            Description = "1-based snapshot index from 'snapshot list' (1 = newest).",
        };
        var snapshotRestoreCommand = new Command("restore", "Restores a mounted disk's contents from a snapshot, replacing its current contents.");
        snapshotRestoreCommand.Arguments.Add(snapshotRestoreDriveArgument);
        snapshotRestoreCommand.Arguments.Add(snapshotRestoreIndexArgument);
        snapshotRestoreCommand.SetAction(async (parseResult, _) =>
            await SnapshotRestoreAsync(
                parseResult.GetValue(snapshotRestoreDriveArgument)!,
                parseResult.GetValue(snapshotRestoreIndexArgument),
                diskController,
                o => outcome = o));

        var snapshotDeleteDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter of a currently mounted disk, e.g. R:",
        };
        var snapshotDeleteIndexArgument = new Argument<int>("index")
        {
            Description = "1-based snapshot index from 'snapshot list' (1 = newest).",
        };
        var snapshotDeleteCommand = new Command("delete", "Deletes a single snapshot of a mounted disk.");
        snapshotDeleteCommand.Arguments.Add(snapshotDeleteDriveArgument);
        snapshotDeleteCommand.Arguments.Add(snapshotDeleteIndexArgument);
        snapshotDeleteCommand.SetAction(async (parseResult, _) =>
            await SnapshotDeleteAsync(
                parseResult.GetValue(snapshotDeleteDriveArgument)!,
                parseResult.GetValue(snapshotDeleteIndexArgument),
                diskController,
                o => outcome = o));

        var snapshotCommand = new Command("snapshot", "Manages timestamped snapshots of a mounted disk's backing image.");
        snapshotCommand.Subcommands.Add(snapshotCreateCommand);
        snapshotCommand.Subcommands.Add(snapshotListCommand);
        snapshotCommand.Subcommands.Add(snapshotRestoreCommand);
        snapshotCommand.Subcommands.Add(snapshotDeleteCommand);

        var cloneSourceDriveArgument = new Argument<string>("source-drive-letter")
        {
            Description = "Drive letter of the mounted disk to copy content from, e.g. R:",
        };
        var cloneTargetDriveArgument = new Argument<string>("target-drive-letter")
        {
            Description = "Drive letter of the mounted, writable disk to overwrite, e.g. S:",
        };
        var cloneCommand = new Command("clone", "Replaces a mounted disk's contents with a copy of another mounted disk's current contents.");
        cloneCommand.Arguments.Add(cloneSourceDriveArgument);
        cloneCommand.Arguments.Add(cloneTargetDriveArgument);
        cloneCommand.SetAction(async (parseResult, _) =>
            await CloneAsync(
                parseResult.GetValue(cloneSourceDriveArgument)!,
                parseResult.GetValue(cloneTargetDriveArgument)!,
                diskController,
                o => outcome = o));

        var exportDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter of a currently mounted disk, e.g. R:",
        };
        var exportOutputArgument = new Argument<string>("output-path")
        {
            Description = "Path to write the exported .mdr image or archive to.",
        };
        var exportFormatOption = new Option<ArchiveExportFormat?>("--format")
        {
            Description = "Export as an archive instead of a .mdr image: Zip or SevenZip.",
        };
        var exportCompressionOption = new Option<ImageCompressionLevel>("--compression")
        {
            DefaultValueFactory = _ => ImageCompressionLevel.Fastest,
            Description = "Compression level: None, Fastest, Optimal, or SmallestSize.",
        };
        var exportPasswordOption = new Option<string?>("--password")
        {
            Description = "Encrypt the exported .mdr image with this password. Not valid together with --format.",
        };
        var exportPasswordFileOption = new Option<string?>("--password-file")
        {
            Description = "Path to a file whose first line is the password to encrypt the exported .mdr image with. Mutually exclusive with --password.",
        };

        var exportCommand = new Command("export", "Exports a mounted disk to a standalone .mdr image or archive file.");
        exportCommand.Arguments.Add(exportDriveArgument);
        exportCommand.Arguments.Add(exportOutputArgument);
        exportCommand.Options.Add(exportFormatOption);
        exportCommand.Options.Add(exportCompressionOption);
        exportCommand.Options.Add(exportPasswordOption);
        exportCommand.Options.Add(exportPasswordFileOption);
        exportCommand.SetAction(async (parseResult, _) =>
        {
            var format = parseResult.GetValue(exportFormatOption);
            var password = parseResult.GetValue(exportPasswordOption);
            var passwordFile = parseResult.GetValue(exportPasswordFileOption);

            if (password is not null && passwordFile is not null)
            {
                outcome = new CliOutcome(false, "--password and --password-file cannot both be specified.", null, 1);
                return 1;
            }

            if ((password is not null || passwordFile is not null) && format is not null)
            {
                outcome = new CliOutcome(false, "--password/--password-file cannot be used together with --format.", null, 1);
                return 1;
            }

            if (passwordFile is not null && !TryReadPasswordFile(passwordFile, out password, out var readError))
            {
                outcome = new CliOutcome(false, readError!, null, 1);
                return 1;
            }

            var exitCode = await ExportAsync(
                parseResult.GetValue(exportDriveArgument)!,
                parseResult.GetValue(exportOutputArgument)!,
                format,
                parseResult.GetValue(exportCompressionOption),
                password,
                diskController,
                o => outcome = o);
            return exitCode;
        });

        var listJsonOption = new Option<bool>("--json")
        {
            Description = "Output the disk list as JSON instead of a table.",
        };
        var listCommand = new Command("list", "Lists currently mounted disks.");
        listCommand.Options.Add(listJsonOption);
        listCommand.SetAction((parseResult, _) =>
        {
            var disks = diskController.ListDisks();
            outcome = new(true, string.Empty, disks, 0, Json: parseResult.GetValue(listJsonOption));
            return Task.FromResult(0);
        });

        var exitCommand = new Command("exit", "Exits the running ManagedDrive application.");
        exitCommand.SetAction(async (_, _) =>
        {
            await diskController.RequestExitAsync();
            outcome = new(true, "ManagedDrive is exiting.", null, 0);
            return 0;
        });

        var rootCommand = new RootCommand("ManagedDrive CLI — quick mount/unmount for RAM disks.");
        rootCommand.Subcommands.Add(mountCommand);
        rootCommand.Subcommands.Add(mountArchiveCommand);
        rootCommand.Subcommands.Add(createCommand);
        rootCommand.Subcommands.Add(cloneCommand);
        rootCommand.Subcommands.Add(unmountCommand);
        rootCommand.Subcommands.Add(formatCommand);
        rootCommand.Subcommands.Add(saveCommand);
        rootCommand.Subcommands.Add(setPasswordCommand);
        rootCommand.Subcommands.Add(exportCommand);
        rootCommand.Subcommands.Add(listCommand);
        rootCommand.Subcommands.Add(lsCommand);
        rootCommand.Subcommands.Add(snapshotCommand);
        rootCommand.Subcommands.Add(exitCommand);

        var invocationConfiguration = new InvocationConfiguration
        {
            Output = buffer,
            Error = buffer,
        };

        var exitCode = await rootCommand.Parse(args).InvokeAsync(invocationConfiguration);

        if (outcome != null)
        {
            return outcome;
        }

        // No handler ran to completion (parse error, --help, unknown subcommand, etc.) — fall
        // back to whatever System.CommandLine wrote to the buffer.
        return new(exitCode == 0, buffer.ToString(), null, exitCode);
    }

    private static async Task<int> CreateAsync(
        string driveLetter, ulong capacityBytes, string? volumeLabel, string? imagePath, string? password,
        ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        var (success, message) = await diskController.CreateAsync(driveLetter, capacityBytes, volumeLabel, imagePath, password);
        setOutcome(new(success, message, null, success ? 0 : 1));
        return success ? 0 : 1;
    }

    private static async Task<int> CloneAsync(string sourceDriveLetter, string targetDriveLetter, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        sourceDriveLetter = NormalizeDriveLetter(sourceDriveLetter);
        targetDriveLetter = NormalizeDriveLetter(targetDriveLetter);

        var (success, message) = await diskController.CloneAsync(sourceDriveLetter, targetDriveLetter);
        setOutcome(new(success, message, null, success ? 0 : 1));
        return success ? 0 : 1;
    }

    private static async Task<int> SnapshotCreateAsync(string driveLetter, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        var (success, message) = await diskController.CreateSnapshotAsync(driveLetter);
        setOutcome(new(
            success,
            string.IsNullOrEmpty(message) ? $"No disk is currently mounted at {driveLetter}." : message,
            null,
            success ? 0 : 1));
        return success ? 0 : 1;
    }

    private static async Task<int> LsAsync(string driveLetter, string? path, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        var (success, message, entries) = await diskController.ListFilesAsync(driveLetter, path);
        if (!success)
        {
            setOutcome(new(
                false,
                string.IsNullOrEmpty(message) ? $"No disk is currently mounted at {driveLetter}." : message,
                null,
                1));
            return 1;
        }

        var lines = entries!.Select(e => e.IsDirectory ? $"{e.Name}/" : $"{e.Name}\t{e.SizeBytes}");
        setOutcome(new(true, string.Join('\n', lines), null, 0));
        return 0;
    }

    private static async Task<int> ExportAsync(
        string driveLetter, string outputPath, ArchiveExportFormat? archiveFormat, ImageCompressionLevel compressionLevel,
        string? password, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        var (success, message) = await diskController.ExportAsync(driveLetter, outputPath, archiveFormat, compressionLevel, password);
        setOutcome(new(
            success,
            string.IsNullOrEmpty(message) ? $"No disk is currently mounted at {driveLetter}." : message,
            null,
            success ? 0 : 1));
        return success ? 0 : 1;
    }

    private static async Task<int> FormatAsync(string driveLetter, bool confirmed, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        if (!confirmed)
        {
            setOutcome(new(false, $"Formatting {driveLetter} will permanently delete all files. Re-run with --yes to confirm.", null, 1));
            return 1;
        }

        var (success, message) = await diskController.FormatAsync(driveLetter);
        if (success)
        {
            setOutcome(new(true, message, null, 0));
            return 0;
        }

        setOutcome(new(
            false,
            string.IsNullOrEmpty(message) ? $"No disk is currently mounted at {driveLetter}." : message,
            null,
            1));
        return 1;
    }

    private static async Task<int> MountArchiveAsync(string archivePath, string? driveLetter, CliMountOverrides overrides, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = driveLetter == null ? null : NormalizeDriveLetter(driveLetter);

        if (!File.Exists(archivePath))
        {
            setOutcome(new(false, $"Archive file not found: {archivePath}", null, 1));
            return 1;
        }

        var (success, message) = await diskController.MountArchiveAsync(archivePath, driveLetter, overrides);
        setOutcome(new(success, message, null, success ? 0 : 1));
        return success ? 0 : 1;
    }

    private static async Task<int> MountAsync(string imagePath, string driveLetter, CliMountOverrides overrides, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        if (!File.Exists(imagePath))
        {
            setOutcome(new(false, $"Image file not found: {imagePath}", null, 1));
            return 1;
        }

        var (success, message) = await diskController.MountImageAsync(imagePath, driveLetter, overrides);
        setOutcome(new(success, message, null, success ? 0 : 1));
        return success ? 0 : 1;
    }

    /// <summary>
    /// Reads the first line of <paramref name="passwordFile"/> as a password, for the
    /// <c>--password-file</c> option shared by <c>mount</c>, <c>export</c>, and <c>set-password</c>.
    /// </summary>
    /// <param name="passwordFile">Path to the file whose first line is the password.</param>
    /// <param name="password">The read password on success; <see langword="null"/> on failure.</param>
    /// <param name="error">A human-readable message on failure; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on success.</returns>
    private static bool TryReadPasswordFile(string passwordFile, out string? password, out string? error)
    {
        try
        {
            password = File.ReadLines(passwordFile).FirstOrDefault() ?? string.Empty;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            password = null;
            error = $"Could not read --password-file: {ex.Message}";
            return false;
        }
    }

    private static string NormalizeDriveLetter(string input)
    {
        input = input.Trim();
        if (input.Length == 1 && char.IsLetter(input[0]))
        {
            return $"{char.ToUpperInvariant(input[0])}:";
        }

        if (input.Length >= 2 && char.IsLetter(input[0]) && input[1] == ':')
        {
            return char.ToUpperInvariant(input[0]) + input[1..];
        }

        return input;
    }

    private static async Task<int> SaveAsync(string driveLetter, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        var (success, message) = await diskController.SaveAsync(driveLetter);
        if (success)
        {
            setOutcome(new(true, message, null, 0));
            return 0;
        }

        setOutcome(new(
            false,
            string.IsNullOrEmpty(message) ? $"No disk is currently mounted at {driveLetter}." : message,
            null,
            1));
        return 1;
    }

    private static async Task<int> SetPasswordAsync(string driveLetter, string? newPassword, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        var (success, message) = await diskController.SetPasswordAsync(driveLetter, newPassword);
        setOutcome(new(
            success,
            string.IsNullOrEmpty(message) ? $"No disk is currently mounted at {driveLetter}." : message,
            null,
            success ? 0 : 1));
        return success ? 0 : 1;
    }

    private static async Task<int> SnapshotDeleteAsync(string driveLetter, int index, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        var (success, message) = await diskController.DeleteSnapshotAsync(driveLetter, index);
        setOutcome(new(
            success,
            string.IsNullOrEmpty(message) ? $"No disk is currently mounted at {driveLetter}." : message,
            null,
            success ? 0 : 1));
        return success ? 0 : 1;
    }

    private static async Task<int> SnapshotListAsync(string driveLetter, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        var (success, message, snapshots) = await diskController.ListSnapshotsAsync(driveLetter);
        if (!success)
        {
            setOutcome(new(
                false,
                string.IsNullOrEmpty(message) ? $"No disk is currently mounted at {driveLetter}." : message,
                null,
                1));
            return 1;
        }

        setOutcome(new(true, message, null, 0, snapshots));
        return 0;
    }

    private static async Task<int> SnapshotRestoreAsync(string driveLetter, int index, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        var (success, message) = await diskController.RestoreSnapshotAsync(driveLetter, index);
        setOutcome(new(
            success,
            string.IsNullOrEmpty(message) ? $"No disk is currently mounted at {driveLetter}." : message,
            null,
            success ? 0 : 1));
        return success ? 0 : 1;
    }

    private static async Task<int> UnmountAsync(string driveLetter, bool deleteImage, ICliDiskController diskController, Action<CliOutcome> setOutcome)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        var unmounted = await diskController.UnmountAsync(driveLetter, deleteImage);
        if (unmounted)
        {
            setOutcome(new(
                true,
                deleteImage ? $"Unmounted {driveLetter} and deleted its image file." : $"Unmounted {driveLetter}.",
                null,
                0));
            return 0;
        }

        setOutcome(new(false, $"No disk is currently mounted at {driveLetter}.", null, 1));
        return 1;
    }
}

/// <summary>
/// Structured result of a completed CLI command: success/failure, a human-readable message,
/// an optional disk list (populated only by <c>list</c>), and the process exit code. Rendering
/// this into terminal output (colors, tables) is the caller's responsibility.
/// </summary>
public sealed record CliOutcome(
    bool Success,
    string Message,
    IReadOnlyList<CliDiskInfo>? Disks,
    int ExitCode,
    IReadOnlyList<CliSnapshotInfo>? Snapshots = null,
    bool Json = false);
