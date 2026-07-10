using ManagedDrive.Core;
using Spectre.Console;
using System.CommandLine;

namespace ManagedDrive.Cli.Core;

/// <summary>
/// Parses and executes ManagedDrive's CLI subcommands (<c>mount</c>, <c>unmount</c>,
/// <c>list</c>, <c>exit</c>) using <c>System.CommandLine</c> for parsing and <c>Spectre.Console</c> for
/// output. Rendered output is captured into an in-memory buffer rather than written to the
/// real console, so it can be shipped back across the CLI named pipe or, on a fresh launch,
/// printed directly.
/// </summary>
public static class CliCommandProcessor
{
    /// <summary>
    /// Parses <paramref name="args"/> and executes the matching subcommand against
    /// <paramref name="diskController"/>.
    /// </summary>
    public static async Task<CliResult> ExecuteAsync(string[] args, ICliDiskController diskController)
    {
        var buffer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(buffer),
        });

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
        mountCommand.SetAction((parseResult, _) =>
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

            return MountAsync(
                parseResult.GetValue(mountImageArgument)!,
                parseResult.GetValue(mountDriveArgument)!,
                overrides,
                diskController,
                console);
        });

        var mountArchiveArgument = new Argument<string>("archive-path")
        {
            Description = "Path to an archive file (zip, 7z, rar, tar, ...) to mount as a read-only disk.",
        };
        var mountArchiveDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter to mount at, e.g. R:",
        };
        var mountArchiveAutoMountOption = new Option<bool?>("--auto-mount")
        {
            Description = "Re-mount this disk automatically on next app startup. If omitted, keeps the saved profile's value (or the default: off).",
        };

        var mountArchiveCommand = new Command("mount-archive", "Mounts the contents of an archive file as a new read-only disk.");
        mountArchiveCommand.Arguments.Add(mountArchiveArgument);
        mountArchiveCommand.Arguments.Add(mountArchiveDriveArgument);
        mountArchiveCommand.Options.Add(mountArchiveAutoMountOption);
        mountArchiveCommand.SetAction((parseResult, _) =>
        {
            var overrides = new CliMountOverrides
            {
                AutoMount = parseResult.GetValue(mountArchiveAutoMountOption),
            };

            return MountArchiveAsync(
                parseResult.GetValue(mountArchiveArgument)!,
                parseResult.GetValue(mountArchiveDriveArgument)!,
                overrides,
                diskController,
                console);
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
        unmountCommand.SetAction((parseResult, _) =>
            UnmountAsync(
                parseResult.GetValue(unmountDriveArgument)!,
                parseResult.GetValue(unmountDeleteImageOption),
                diskController,
                console));

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
        formatCommand.SetAction((parseResult, _) =>
            FormatAsync(
                parseResult.GetValue(formatDriveArgument)!,
                parseResult.GetValue(formatYesOption),
                diskController,
                console));

        var saveDriveArgument = new Argument<string>("drive-letter")
        {
            Description = "Drive letter of a currently mounted disk, e.g. R:",
        };
        var saveCommand = new Command("save", "Saves a mounted disk's contents to its backing .mdr image immediately.");
        saveCommand.Arguments.Add(saveDriveArgument);
        saveCommand.SetAction((parseResult, _) =>
            SaveAsync(parseResult.GetValue(saveDriveArgument)!, diskController, console));

        var listCommand = new Command("list", "Lists currently mounted disks.");
        listCommand.SetAction((_, _) => Task.FromResult(RenderList(diskController, console)));

        var exitCommand = new Command("exit", "Exits the running ManagedDrive application.");
        exitCommand.SetAction((_, _) => ExitAsync(diskController, console));

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
        return new CliResult(buffer.ToString(), exitCode);
    }

    private static async Task<int> ExitAsync(ICliDiskController diskController, IAnsiConsole console)
    {
        await diskController.RequestExitAsync();
        console.MarkupLine("[green]ManagedDrive is exiting.[/]");
        return 0;
    }

    private static async Task<int> FormatAsync(string driveLetter, bool confirmed, ICliDiskController diskController, IAnsiConsole console)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        if (!confirmed)
        {
            console.MarkupLine($"[red]Formatting {driveLetter} will permanently delete all files. Re-run with --yes to confirm.[/]");
            return 1;
        }

        var (success, message) = await diskController.FormatAsync(driveLetter);
        if (success)
        {
            console.MarkupLine($"[green]{Markup.Escape(message)}[/]");
            return 0;
        }

        console.MarkupLine(string.IsNullOrEmpty(message)
            ? $"[red]No disk is currently mounted at {driveLetter}.[/]"
            : $"[red]{Markup.Escape(message)}[/]");
        return 1;
    }

    private static async Task<int> MountAsync(string imagePath, string driveLetter, CliMountOverrides overrides, ICliDiskController diskController, IAnsiConsole console)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        if (!File.Exists(imagePath))
        {
            console.MarkupLine($"[red]Image file not found: {Markup.Escape(imagePath)}[/]");
            return 1;
        }

        var (success, message) = await diskController.MountImageAsync(imagePath, driveLetter, overrides);
        console.MarkupLine(success ? $"[green]{Markup.Escape(message)}[/]" : $"[red]{Markup.Escape(message)}[/]");
        return success ? 0 : 1;
    }

    private static async Task<int> MountArchiveAsync(string archivePath, string driveLetter, CliMountOverrides overrides, ICliDiskController diskController, IAnsiConsole console)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        if (!File.Exists(archivePath))
        {
            console.MarkupLine($"[red]Archive file not found: {Markup.Escape(archivePath)}[/]");
            return 1;
        }

        var (success, message) = await diskController.MountArchiveAsync(archivePath, driveLetter, overrides);
        console.MarkupLine(success ? $"[green]{Markup.Escape(message)}[/]" : $"[red]{Markup.Escape(message)}[/]");
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

    private static int RenderList(ICliDiskController diskController, IAnsiConsole console)
    {
        var disks = diskController.ListDisks();
        if (disks.Count == 0)
        {
            console.MarkupLine("[yellow]No disks are currently mounted.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Mount Point");
        table.AddColumn("Label");
        table.AddColumn("Used");
        table.AddColumn("Capacity");

        foreach (var disk in disks)
        {
            table.AddRow(
                disk.MountPoint,
                Markup.Escape(disk.VolumeLabel),
                ByteFormatter.Format(disk.UsedBytes),
                ByteFormatter.Format(disk.TotalBytes));
        }

        console.Write(table);
        return 0;
    }

    private static async Task<int> SaveAsync(string driveLetter, ICliDiskController diskController, IAnsiConsole console)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        var (success, message) = await diskController.SaveAsync(driveLetter);
        if (success)
        {
            console.MarkupLine($"[green]{Markup.Escape(message)}[/]");
            return 0;
        }

        console.MarkupLine(string.IsNullOrEmpty(message)
            ? $"[red]No disk is currently mounted at {driveLetter}.[/]"
            : $"[red]{Markup.Escape(message)}[/]");
        return 1;
    }

    private static async Task<int> UnmountAsync(string driveLetter, bool deleteImage, ICliDiskController diskController, IAnsiConsole console)
    {
        driveLetter = NormalizeDriveLetter(driveLetter);

        var unmounted = await diskController.UnmountAsync(driveLetter, deleteImage);
        if (unmounted)
        {
            console.MarkupLine(deleteImage
                ? $"[green]Unmounted {driveLetter} and deleted its image file.[/]"
                : $"[green]Unmounted {driveLetter}.[/]");
            return 0;
        }

        console.MarkupLine($"[red]No disk is currently mounted at {driveLetter}.[/]");
        return 1;
    }
}

/// <summary>
/// Rendered output plus process exit code of a completed CLI command.
/// </summary>
public sealed record CliResult(string Output, int ExitCode);