using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliCommandProcessorMountTests
{
    [Fact]
    public async Task Mount_ForwardsImagePathDriveLetterAndOverrides_ToController()
    {
        var imagePath = CreateTempImageFile();
        try
        {
            var controller = new FakeCliDiskController { MountSuccess = true, MountMessage = "Mounted R:." };

            var outcome = await CliCommandProcessor.ExecuteAsync(
                ["mount", imagePath, "r", "--read-only"],
                controller);

            Assert.True(outcome.Success);
            Assert.Equal(0, outcome.ExitCode);
            Assert.Equal(imagePath, controller.LastImagePath);
            Assert.Equal("R:", controller.LastMountPoint);
            Assert.True(controller.LastOverrides?.ReadOnly);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task Mount_Failure_ReturnsErrorMessageAndNonZeroExitCode()
    {
        var imagePath = CreateTempImageFile();
        try
        {
            var controller = new FakeCliDiskController { MountSuccess = false, MountMessage = "Wrong password." };

            var outcome = await CliCommandProcessor.ExecuteAsync(["mount", imagePath, "R:"], controller);

            Assert.False(outcome.Success);
            Assert.Equal(1, outcome.ExitCode);
            Assert.Equal("Wrong password.", outcome.Message);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task Unmount_NormalizesDriveLetter_ForwardsDeleteImageFlag()
    {
        var controller = new FakeCliDiskController { UnmountSuccess = true };

        var outcome = await CliCommandProcessor.ExecuteAsync(["unmount", "r", "--delete-image"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastMountPoint);
        Assert.True(controller.LastDeleteImage);
    }

    [Fact]
    public async Task List_ReturnsDisksFromController()
    {
        var controller = new FakeCliDiskController
        {
            Disks = [new CliDiskInfo("R:", "MyDisk", 1024, 4096)],
        };

        var outcome = await CliCommandProcessor.ExecuteAsync(["list"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Single(outcome.Disks!);
        Assert.Equal("R:", outcome.Disks![0].MountPoint);
    }

    [Fact]
    public async Task Exit_InvokesRequestExitAsync()
    {
        var controller = new FakeCliDiskController();

        var outcome = await CliCommandProcessor.ExecuteAsync(["exit"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.True(controller.ExitRequested);
    }

    private static string CreateTempImageFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        File.WriteAllBytes(path, []);
        return path;
    }

    private sealed class FakeCliDiskController : ICliDiskController
    {
        public string? LastImagePath
        {
            get; private set;
        }

        public string? LastMountPoint
        {
            get; private set;
        }

        public CliMountOverrides? LastOverrides
        {
            get; private set;
        }

        public bool LastDeleteImage
        {
            get; private set;
        }

        public bool ExitRequested
        {
            get; private set;
        }

        public bool MountSuccess { get; set; } = true;

        public string MountMessage { get; set; } = string.Empty;

        public bool UnmountSuccess { get; set; } = true;

        public IReadOnlyList<CliDiskInfo> Disks { get; set; } = [];

        public Task<(bool Success, string Message)> DeleteSnapshotAsync(string mountPoint, int index) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> FormatAsync(string mountPoint) =>
            Task.FromResult((false, string.Empty));

        public IReadOnlyList<CliDiskInfo> ListDisks() => Disks;

        public Task<(bool Success, string Message, IReadOnlyList<CliSnapshotInfo>? Snapshots)> ListSnapshotsAsync(string mountPoint) =>
            Task.FromResult<(bool, string, IReadOnlyList<CliSnapshotInfo>?)>((false, string.Empty, null));

        public Task<(bool Success, string Message)> MountArchiveAsync(string archivePath, string? mountPoint, CliMountOverrides overrides) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> MountImageAsync(string imagePath, string mountPoint, CliMountOverrides overrides)
        {
            LastImagePath = imagePath;
            LastMountPoint = mountPoint;
            LastOverrides = overrides;
            return Task.FromResult((MountSuccess, MountMessage));
        }

        public Task<(bool Success, string Message)> RestoreSnapshotAsync(string mountPoint, int index) =>
            Task.FromResult((false, string.Empty));

        public Task RequestExitAsync()
        {
            ExitRequested = true;
            return Task.CompletedTask;
        }

        public Task<(bool Success, string Message)> SaveAsync(string mountPoint) =>
            Task.FromResult((false, string.Empty));

        public Task<bool> UnmountAsync(string mountPoint, bool deleteImage)
        {
            LastMountPoint = mountPoint;
            LastDeleteImage = deleteImage;
            return Task.FromResult(UnmountSuccess);
        }
    }
}
