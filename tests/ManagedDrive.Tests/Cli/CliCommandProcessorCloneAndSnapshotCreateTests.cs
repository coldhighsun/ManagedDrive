using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliCommandProcessorCloneAndSnapshotCreateTests
{
    [Fact]
    public async Task Clone_NormalizesBothDriveLetters()
    {
        var controller = new FakeCliDiskController { CloneSuccess = true, CloneMessage = "Cloned R: to S:" };

        var outcome = await CliCommandProcessor.ExecuteAsync(["clone", "r", "s"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastSourceMountPoint);
        Assert.Equal("S:", controller.LastTargetMountPoint);
    }

    [Fact]
    public async Task Clone_SourceEqualsTarget_ForwardsBothMountPointsToController()
    {
        // The self-clone guard lives in MainViewModel.CloneByMountPointAsync (compares the two
        // resolved DiskViewModel instances), not in CliCommandProcessor, so at this layer the
        // request is simply forwarded — this only verifies both arguments reach the controller
        // unchanged when they normalize to the same drive letter.
        var controller = new FakeCliDiskController { CloneSuccess = false, CloneMessage = "The clone target cannot be the same disk as the source." };

        var outcome = await CliCommandProcessor.ExecuteAsync(["clone", "r", "R:"], controller);

        Assert.False(outcome.Success);
        Assert.Equal("R:", controller.LastSourceMountPoint);
        Assert.Equal("R:", controller.LastTargetMountPoint);
    }

    [Fact]
    public async Task Clone_Failure_ReturnsErrorMessageAndNonZeroExitCode()
    {
        var controller = new FakeCliDiskController { CloneSuccess = false, CloneMessage = "S: is read-only and cannot be a clone target." };

        var outcome = await CliCommandProcessor.ExecuteAsync(["clone", "R:", "S:"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("S: is read-only and cannot be a clone target.", outcome.Message);
    }

    [Fact]
    public async Task SnapshotCreate_NormalizesDriveLetter()
    {
        var controller = new FakeCliDiskController { SnapshotCreateSuccess = true, SnapshotCreateMessage = "Image saved for R:." };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "create", "r"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastSnapshotCreateMountPoint);
    }

    [Fact]
    public async Task SnapshotCreate_NotMounted_ReturnsNotMountedMessage()
    {
        var controller = new FakeCliDiskController { SnapshotCreateSuccess = false, SnapshotCreateMessage = string.Empty };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "create", "Z:"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("Z:", outcome.Message);
    }

    [Fact]
    public async Task SnapshotCreate_Failure_ReturnsErrorMessage()
    {
        var controller = new FakeCliDiskController
        {
            SnapshotCreateSuccess = false,
            SnapshotCreateMessage = "Snapshot retention is not configured for this disk.",
        };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "create", "R:"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("Snapshot retention is not configured for this disk.", outcome.Message);
    }

    private sealed class FakeCliDiskController : ICliDiskController
    {
        public string? LastSourceMountPoint
        {
            get; private set;
        }

        public string? LastTargetMountPoint
        {
            get; private set;
        }

        public bool CloneSuccess { get; set; } = true;

        public string CloneMessage { get; set; } = string.Empty;

        public Task<(bool Success, string Message)> CloneAsync(string sourceMountPoint, string targetMountPoint)
        {
            LastSourceMountPoint = sourceMountPoint;
            LastTargetMountPoint = targetMountPoint;
            return Task.FromResult((CloneSuccess, CloneMessage));
        }

        public string? LastSnapshotCreateMountPoint
        {
            get; private set;
        }

        public bool SnapshotCreateSuccess { get; set; } = true;

        public string SnapshotCreateMessage { get; set; } = string.Empty;

        public Task<(bool Success, string Message)> CreateSnapshotAsync(string mountPoint)
        {
            LastSnapshotCreateMountPoint = mountPoint;
            return Task.FromResult((SnapshotCreateSuccess, SnapshotCreateMessage));
        }

        public Task<(bool Success, string Message)> CreateAsync(string mountPoint, ulong capacityBytes, string? volumeLabel, string? imagePath, string? password) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message, CliSnapshotDiff? Diff)> DiffSnapshotAsync(string mountPoint, int index) =>
            Task.FromResult<(bool, string, CliSnapshotDiff?)>((false, string.Empty, null));

        public Task<(bool Success, string Message)> EditAsync(string mountPoint, ulong? capacityBytes, string? volumeLabel, uint? autoSaveIntervalMinutes, bool disableAutoSave) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message, IReadOnlyList<CliFileEntry>? Entries)> ListFilesAsync(string mountPoint, string? path) =>
            Task.FromResult<(bool, string, IReadOnlyList<CliFileEntry>?)>((false, string.Empty, null));

        public Task<(bool Success, string Message)> DeleteSnapshotAsync(string mountPoint, int index) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> ExportAsync(string mountPoint, string outputPath, ManagedDrive.Cli.Core.ArchiveExportFormat? archiveFormat, ManagedDrive.Cli.Core.ImageCompressionLevel compressionLevel, string? password) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> FormatAsync(string mountPoint) =>
            Task.FromResult((false, string.Empty));

        public IReadOnlyList<CliDiskInfo> ListDisks() => [];

        public Task<(bool Success, string Message, IReadOnlyList<CliSnapshotInfo>? Snapshots)> ListSnapshotsAsync(string mountPoint) =>
            Task.FromResult<(bool, string, IReadOnlyList<CliSnapshotInfo>?)>((false, string.Empty, null));

        public Task<(bool Success, string Message)> MountArchiveAsync(string archivePath, string? mountPoint, CliMountOverrides overrides) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> MountImageAsync(string imagePath, string mountPoint, CliMountOverrides overrides) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> RestoreSnapshotAsync(string mountPoint, int index) =>
            Task.FromResult((false, string.Empty));

        public Task RequestExitAsync() => Task.CompletedTask;

        public Task<(bool Success, string Message)> SaveAsync(string mountPoint) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> SetPasswordAsync(string mountPoint, string? newPassword) =>
            Task.FromResult((false, string.Empty));

        public Task<bool> UnmountAsync(string mountPoint, bool deleteImage) => Task.FromResult(false);
    }
}
