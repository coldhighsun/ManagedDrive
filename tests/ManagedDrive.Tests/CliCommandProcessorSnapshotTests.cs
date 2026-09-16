using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliCommandProcessorSnapshotTests
{
    [Fact]
    public async Task SnapshotList_ForwardsSnapshotsInOutcome()
    {
        var controller = new FakeCliDiskController
        {
            Snapshots =
            [
                new CliSnapshotInfo(1, DateTimeOffset.UtcNow, 1024),
                new CliSnapshotInfo(2, DateTimeOffset.UtcNow.AddDays(-1), 2048),
            ],
        };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "list", "R:"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastMountPoint);
        Assert.Equal(2, outcome.Snapshots?.Count);
    }

    [Fact]
    public async Task SnapshotList_NotMounted_ReturnsNotMountedMessage()
    {
        var controller = new FakeCliDiskController { ListSnapshotsSuccess = false };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "list", "Z:"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("Z:", outcome.Message);
    }

    [Fact]
    public async Task SnapshotRestore_NormalizesDriveLetterAndForwardsIndex()
    {
        var controller = new FakeCliDiskController { RestoreSuccess = true, RestoreMessage = "Restored." };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "restore", "r", "2"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastMountPoint);
        Assert.Equal(2, controller.LastIndex);
        Assert.Equal("Restored.", outcome.Message);
    }

    [Fact]
    public async Task SnapshotRestore_Failure_ReturnsErrorMessageAndNonZeroExitCode()
    {
        var controller = new FakeCliDiskController { RestoreSuccess = false, RestoreMessage = "Invalid snapshot index." };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "restore", "R:", "99"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("Invalid snapshot index.", outcome.Message);
    }

    [Fact]
    public async Task SnapshotDelete_ForwardsMountPointAndIndex()
    {
        var controller = new FakeCliDiskController { DeleteSuccess = true, DeleteMessage = "Deleted a snapshot of R:." };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "delete", "R:", "1"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastMountPoint);
        Assert.Equal(1, controller.LastIndex);
    }

    private sealed class FakeCliDiskController : ICliDiskController
    {
        public string? LastMountPoint
        {
            get; private set;
        }

        public int LastIndex
        {
            get; private set;
        }

        public IReadOnlyList<CliSnapshotInfo> Snapshots { get; set; } = [];

        public bool ListSnapshotsSuccess { get; set; } = true;

        public bool RestoreSuccess { get; set; } = true;

        public string RestoreMessage { get; set; } = string.Empty;

        public bool DeleteSuccess { get; set; } = true;

        public string DeleteMessage { get; set; } = string.Empty;

        public Task<(bool Success, string Message)> DeleteSnapshotAsync(string mountPoint, int index)
        {
            LastMountPoint = mountPoint;
            LastIndex = index;
            return Task.FromResult((DeleteSuccess, DeleteMessage));
        }

        public Task<(bool Success, string Message)> ExportAsync(string mountPoint, string outputPath, ManagedDrive.Cli.Core.ArchiveExportFormat? archiveFormat, ManagedDrive.Cli.Core.ImageCompressionLevel compressionLevel, string? password) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> FormatAsync(string mountPoint) =>
            Task.FromResult((false, string.Empty));

        public IReadOnlyList<CliDiskInfo> ListDisks() => [];

        public Task<(bool Success, string Message, IReadOnlyList<CliSnapshotInfo>? Snapshots)> ListSnapshotsAsync(string mountPoint)
        {
            LastMountPoint = mountPoint;
            return Task.FromResult<(bool, string, IReadOnlyList<CliSnapshotInfo>?)>(
                ListSnapshotsSuccess ? (true, string.Empty, Snapshots) : (false, string.Empty, null));
        }

        public Task<(bool Success, string Message)> MountArchiveAsync(string archivePath, string? mountPoint, CliMountOverrides overrides) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> MountImageAsync(string imagePath, string mountPoint, CliMountOverrides overrides) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> RestoreSnapshotAsync(string mountPoint, int index)
        {
            LastMountPoint = mountPoint;
            LastIndex = index;
            return Task.FromResult((RestoreSuccess, RestoreMessage));
        }

        public Task RequestExitAsync() => Task.CompletedTask;

        public Task<(bool Success, string Message)> SaveAsync(string mountPoint) =>
            Task.FromResult((false, string.Empty));

        public Task<bool> UnmountAsync(string mountPoint, bool deleteImage) => Task.FromResult(false);
    }
}
