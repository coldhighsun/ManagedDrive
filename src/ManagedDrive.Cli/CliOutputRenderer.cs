using ManagedDrive.Cli.Core;
using Spectre.Console;

namespace ManagedDrive.Cli;

/// <summary>
/// Renders a <see cref="CliResponse"/> to the real terminal using Spectre.Console (colors,
/// tables). Kept out of <c>ManagedDrive.Cli.Core</c> so <c>ManagedDrive.App</c> — which only
/// needs the plain <see cref="CliResponse.Message"/> for a <c>MessageBox</c> — never pulls in
/// Spectre.Console.
/// </summary>
public static class CliOutputRenderer
{
    public static int Render(CliResponse response)
    {
        if (response.Disks != null)
        {
            RenderDiskList(response.Disks);
            return response.ExitCode;
        }

        if (response.Snapshots != null)
        {
            RenderSnapshotList(response.Snapshots);
            return response.ExitCode;
        }

        if (response.Success)
        {
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(response.Message)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(response.Message)}[/]");
        }

        return response.ExitCode;
    }

    private static void RenderDiskList(IReadOnlyList<CliDiskInfo> disks)
    {
        if (disks.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No disks are currently mounted.[/]");
            return;
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

        AnsiConsole.Write(table);
    }

    private static void RenderSnapshotList(IReadOnlyList<CliSnapshotInfo> snapshots)
    {
        if (snapshots.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No snapshots are available for this disk.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Index");
        table.AddColumn("Timestamp (local)");
        table.AddColumn("Size");

        foreach (var snapshot in snapshots)
        {
            table.AddRow(
                snapshot.Index.ToString(),
                snapshot.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ByteFormatter.Format(snapshot.SizeBytes));
        }

        AnsiConsole.Write(table);
    }
}
