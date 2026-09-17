using ManagedDrive.Cli.Core;
using CliCore = ManagedDrive.Cli.Core;

namespace ManagedDrive.App.Cli;

/// <summary>
/// Adapts <see cref="MainViewModel"/> to <see cref="ICliDiskController"/> so the standalone
/// <c>ManagedDrive.Cli.Core</c> project (which cannot reference the WPF app layer without creating
/// a circular project reference) can drive disk mount/unmount/list operations.
/// </summary>
/// <remarks>
/// This class is the entire contract surface between the CLI pipe server and <see cref="MainViewModel"/>:
/// <see cref="MainViewModel.ExitWithoutConfirmation"/>, <see cref="MainViewModel.FormatByMountPointAsync"/>,
/// <see cref="MainViewModel.MountArchiveAsync"/>, <see cref="MainViewModel.MountImageAsync"/>,
/// <see cref="MainViewModel.SaveByMountPointAsync"/>, <see cref="MainViewModel.SetPasswordByMountPointAsync"/>,
/// <see cref="MainViewModel.ExportByMountPointAsync"/>,
/// <see cref="MainViewModel.UnmountByMountPointAsync"/>,
/// <see cref="MainViewModel.ListSnapshotsByMountPointAsync"/>, <see cref="MainViewModel.RestoreSnapshotByMountPointAsync"/>,
/// <see cref="MainViewModel.DeleteSnapshotByMountPointAsync"/>, <see cref="MainViewModel.CreateByOptionsAsync"/>,
/// <see cref="MainViewModel.ListFilesByMountPointAsync"/>, <see cref="MainViewModel.CloneByMountPointAsync"/>,
/// and <see cref="MainViewModel.CreateSnapshotByMountPointAsync"/> (plus the read-only
/// <see cref="MainViewModel.Disks"/> collection for <see cref="ListDisks"/>). Changing any of their
/// signatures requires updating this adapter in lockstep.
/// </remarks>
internal sealed class MainViewModelCliDiskController(MainViewModel mainViewModel) : ICliDiskController
{
    /// <summary>
    /// How long to wait before actually exiting, so the CLI pipe response reporting success has
    /// time to be written back and the pipe server's accept loop returns to idle — exiting
    /// immediately would race <see cref="App.ShutdownAsync"/>'s <c>CliPipeServer.Dispose()</c>
    /// against the still-in-flight response for this very request.
    /// </summary>
    private static readonly TimeSpan ExitDelay = TimeSpan.FromMilliseconds(300);

    public Task<(bool Success, string Message)> DeleteSnapshotAsync(string mountPoint, int index) =>
        mainViewModel.DeleteSnapshotByMountPointAsync(mountPoint, index);

    public Task<(bool Success, string Message)> ExportAsync(string mountPoint, string outputPath, CliCore.ArchiveExportFormat? archiveFormat, CliCore.ImageCompressionLevel compressionLevel, string? password) =>
        mainViewModel.ExportByMountPointAsync(
            mountPoint,
            outputPath,
            archiveFormat is { } format ? (Core.Archive.ArchiveExportFormat)format : null,
            (Core.Mounting.ImageCompressionLevel)compressionLevel,
            password);

    public Task<(bool Success, string Message)> FormatAsync(string mountPoint) =>
        mainViewModel.FormatByMountPointAsync(mountPoint);

    public IReadOnlyList<CliDiskInfo> ListDisks() =>
            mainViewModel.Disks
            .Select(vm => new CliDiskInfo(vm.MountPoint, vm.VolumeLabel, vm.Disk.UsedBytes, vm.Disk.TotalBytes))
            .ToList();

    public Task<(bool Success, string Message, IReadOnlyList<CliSnapshotInfo>? Snapshots)> ListSnapshotsAsync(string mountPoint) =>
        mainViewModel.ListSnapshotsByMountPointAsync(mountPoint);

    public Task<(bool Success, string Message)> MountArchiveAsync(string archivePath, string? mountPoint, CliMountOverrides overrides) =>
        mainViewModel.MountArchiveAsync(archivePath, mountPoint, overrides);

    public Task<(bool Success, string Message)> MountImageAsync(string imagePath, string mountPoint, CliMountOverrides overrides) =>
            mainViewModel.MountImageAsync(imagePath, mountPoint, overrides);

    public Task<(bool Success, string Message)> CreateAsync(string mountPoint, ulong capacityBytes, string? volumeLabel, string? imagePath, string? password) =>
        mainViewModel.CreateByOptionsAsync(mountPoint, capacityBytes, volumeLabel, imagePath, password);

    public Task<(bool Success, string Message, IReadOnlyList<CliFileEntry>? Entries)> ListFilesAsync(string mountPoint, string? path) =>
        mainViewModel.ListFilesByMountPointAsync(mountPoint, path);

    public Task<(bool Success, string Message)> CloneAsync(string sourceMountPoint, string targetMountPoint) =>
        mainViewModel.CloneByMountPointAsync(sourceMountPoint, targetMountPoint);

    public Task<(bool Success, string Message)> CreateSnapshotAsync(string mountPoint) =>
        mainViewModel.CreateSnapshotByMountPointAsync(mountPoint);

    public Task<(bool Success, string Message)> RestoreSnapshotAsync(string mountPoint, int index) =>
        mainViewModel.RestoreSnapshotByMountPointAsync(mountPoint, index);

    public Task RequestExitAsync()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(ExitDelay);
            await Application.Current.Dispatcher.InvokeAsync(mainViewModel.ExitWithoutConfirmation);
        });

        return Task.CompletedTask;
    }

    public Task<(bool Success, string Message)> SaveAsync(string mountPoint) =>
        mainViewModel.SaveByMountPointAsync(mountPoint);

    public Task<(bool Success, string Message)> SetPasswordAsync(string mountPoint, string? newPassword) =>
        mainViewModel.SetPasswordByMountPointAsync(mountPoint, newPassword);

    public Task<bool> UnmountAsync(string mountPoint, bool deleteImage) =>
                mainViewModel.UnmountByMountPointAsync(mountPoint, deleteImage);
}
