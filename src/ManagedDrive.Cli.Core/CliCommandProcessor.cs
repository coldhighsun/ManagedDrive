using System.CommandLine;

namespace ManagedDrive.Cli.Core;

/// <summary>
/// Parses and dispatches ManagedDrive's CLI subcommands (<c>mount</c>, <c>unmount</c>,
/// <c>list</c>, <c>exit</c>) using <c>System.CommandLine</c>. Returns a structured
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
            Description = "Drive letter to mount at, e.g. R:",
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

        var mountCommand = new Command("mount", "Mounts an existing .mdr disk image at a drive letter.");
        mountCommand.Arguments.Add(mountImageArgument);
        mountCommand.Arguments.Add(mountDriveArgument);
        mountCommand.Options.Add(mountReadOnlyOption);
        mountCommand.Options.Add(mountAutoMountOption);
        mountCommand.Options.Add(mountAutoSaveMinutesOption);
        mountCommand.Options.Add(mountCompressionOption);
        mountCommand.Options.Add(mountMaxSnapshotCountOption);
        mountCommand.Options.Add(mountMaxSnapshotSizeMbOption);
        mountCommand.Options.Add(mountHighUsageWarnPercentOption);
        mountCommand.SetAction(async (parseResult, _) =>
        {
            var maxSnapshotSizeMb = parseResult.GetValue(mountMaxSnapshotSizeMbOption);
            var overrides = new CliMountOverrides
            {
                ReadOnly = parseResult.GetValue(mountReadOnlyOption),
                AutoMount = parseResult.GetValue(mountAutoMountOption),
                AutoSaveIntervalMinutes = parseResult.GetValue(mountAutoSaveMinutesOption),
                CompressionLevel = parseResult.GetValue(mountCompressionOption),
                MaxSnapshotCount = parseResult.GetValue(mountMaxSnapshotCountOption),
                MaxSnapshotSizeBytes = maxSnapshotSizeMb * 1024UL * 1024UL,
                HighUsageWarnPercent = parseResult.GetValue(mountHighUsageWarnPercentOption),
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
            Description = "Drive letter to mount at, e.g. R:. If omitted, the first free letter from Z: down to D: is picked automatically.",
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

        var listCommand = new Command("list", "Lists currently mounted disks.");
        listCommand.SetAction((_, _) =>
        {
            var disks = diskController.ListDisks();
            outcome = new(true, string.Empty, disks, 0);
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
        rootCommand.Subcommands.Add(unmountCommand);
        rootCommand.Subcommands.Add(formatCommand);
        rootCommand.Subcommands.Add(saveCommand);
        rootCommand.Subcommands.Add(listCommand);
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
public sealed record CliOutcome(bool Success, string Message, IReadOnlyList<CliDiskInfo>? Disks, int ExitCode);