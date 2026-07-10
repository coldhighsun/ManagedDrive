namespace ManagedDrive.Cli.Core;

/// <summary>
/// Everything <see cref="CliCommandProcessor"/> needs from the host application to execute CLI
/// subcommands, without depending on the WPF app layer directly (which would create a circular
/// project reference, since the app layer is what hosts the CLI pipe server).
/// </summary>
public interface ICliDiskController
{
    /// <summary>
    /// Formats the disk currently mounted at <paramref name="mountPoint"/>, permanently deleting
    /// all files and folders on it.
    /// </summary>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise. <paramref name="mountPoint"/> not being mounted is reported as
    /// <c>(false, string.Empty)</c> so the CLI layer can render its own not-mounted message.
    /// </returns>
    Task<(bool Success, string Message)> FormatAsync(string mountPoint);

    /// <summary>
    /// Returns a snapshot of all currently mounted disks.
    /// </summary>
    IReadOnlyList<CliDiskInfo> ListDisks();

    /// <summary>
    /// Mounts an existing disk image at <paramref name="mountPoint"/>.
    /// </summary>
    /// <param name="imagePath">Path to an existing <c>.mdr</c> disk image.</param>
    /// <param name="mountPoint">The drive letter to mount at.</param>
    /// <param name="overrides">
    /// Per-field values the user explicitly passed via CLI flags; any <c>null</c> field defers to
    /// a saved profile for <paramref name="imagePath"/> if one exists, or the built-in default.
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise.
    /// </returns>
    Task<(bool Success, string Message)> MountImageAsync(string imagePath, string mountPoint, CliMountOverrides overrides);

    /// <summary>
    /// Requests that the running ManagedDrive application exit. Must not block until the process
    /// has actually shut down — the actual exit should happen after this call returns (e.g. on a
    /// short delay), so the CLI response reporting success can still be written back over the
    /// pipe before the host process starts tearing down its own <c>CliPipeServer</c>.
    /// </summary>
    Task RequestExitAsync();

    /// <summary>
    /// Saves the disk currently mounted at <paramref name="mountPoint"/> to its backing image
    /// file immediately.
    /// </summary>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise. <paramref name="mountPoint"/> not being mounted is reported as
    /// <c>(false, string.Empty)</c> so the CLI layer can render its own not-mounted message.
    /// </returns>
    Task<(bool Success, string Message)> SaveAsync(string mountPoint);

    /// <summary>
    /// Unmounts the disk currently mounted at <paramref name="mountPoint"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> if a mounted disk was found and unmounted; <c>false</c> if no disk is
    /// currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    Task<bool> UnmountAsync(string mountPoint);
}

/// <summary>
/// Read-only snapshot of a mounted disk, as needed to render the CLI <c>list</c> table.
/// </summary>
public sealed record CliDiskInfo(string MountPoint, string VolumeLabel, ulong UsedBytes, ulong TotalBytes);