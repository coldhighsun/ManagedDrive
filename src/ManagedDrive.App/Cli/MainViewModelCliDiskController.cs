namespace ManagedDrive.App.Cli;

/// <summary>
/// Adapts <see cref="MainViewModel"/> to <see cref="ICliDiskController"/> so the standalone
/// <c>ManagedDrive.Cli.Core</c> project (which cannot reference the WPF app layer without creating
/// a circular project reference) can drive disk mount/unmount/list operations.
/// </summary>
internal sealed class MainViewModelCliDiskController(MainViewModel mainViewModel) : ICliDiskController
{
    /// <summary>
    /// How long to wait before actually exiting, so the CLI pipe response reporting success has
    /// time to be written back and the pipe server's accept loop returns to idle — exiting
    /// immediately would race <see cref="App.ShutdownAsync"/>'s <c>CliPipeServer.Dispose()</c>
    /// against the still-in-flight response for this very request.
    /// </summary>
    private static readonly TimeSpan ExitDelay = TimeSpan.FromMilliseconds(300);

    public Task<(bool Success, string Message)> FormatAsync(string mountPoint) =>
        mainViewModel.FormatByMountPointAsync(mountPoint);

    public IReadOnlyList<CliDiskInfo> ListDisks() =>
            mainViewModel.Disks
            .Select(vm => new CliDiskInfo(vm.MountPoint, vm.VolumeLabel, vm.Disk.UsedBytes, vm.Disk.TotalBytes))
            .ToList();

    public Task<(bool Success, string Message)> MountImageAsync(string imagePath, string mountPoint, CliMountOverrides overrides) =>
        mainViewModel.MountImageAsync(imagePath, mountPoint, overrides);

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

    public Task<bool> UnmountAsync(string mountPoint) =>
                mainViewModel.UnmountByMountPointAsync(mountPoint);
}