using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliCommandProcessorEditAndSnapshotDiffTests
{
    [Fact]
    public async Task Edit_ForwardsCapacityLabelAndAutoSaveMinutes()
    {
        var controller = new FakeCliDiskController { EditSuccess = true, EditMessage = "Mounted R: (Data, 512 MB)." };

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["edit", "r", "--capacity-mb", "512", "--label", "Data", "--auto-save-minutes", "5"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastMountPoint);
        Assert.Equal(512UL * 1024 * 1024, controller.LastCapacityBytes);
        Assert.Equal("Data", controller.LastVolumeLabel);
        Assert.Equal(5u, controller.LastAutoSaveIntervalMinutes);
        Assert.False(controller.LastDisableAutoSave);
    }

    [Fact]
    public async Task Edit_DisableAutoSave_ForwardsFlag()
    {
        var controller = new FakeCliDiskController { EditSuccess = true, EditMessage = "Mounted R: (Data, 512 MB)." };

        var outcome = await CliCommandProcessor.ExecuteAsync(["edit", "r", "--disable-auto-save"], controller);

        Assert.True(outcome.Success);
        Assert.True(controller.LastDisableAutoSave);
        Assert.Null(controller.LastAutoSaveIntervalMinutes);
    }

    [Fact]
    public async Task Edit_NoOptionsSpecified_ReturnsErrorWithoutCallingController()
    {
        var controller = new FakeCliDiskController { EditSuccess = true, EditMessage = "should not be used" };

        var outcome = await CliCommandProcessor.ExecuteAsync(["edit", "r"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Null(controller.LastMountPoint);
    }

    [Fact]
    public async Task Edit_BothAutoSaveMinutesAndDisable_ReturnsError()
    {
        var controller = new FakeCliDiskController();

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["edit", "r", "--auto-save-minutes", "5", "--disable-auto-save"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Null(controller.LastMountPoint);
    }

    [Fact]
    public async Task Edit_Failure_ReturnsErrorMessage()
    {
        var controller = new FakeCliDiskController
        {
            EditSuccess = false,
            EditMessage = "Cannot reduce capacity: current usage exceeds the requested capacity.",
        };

        var outcome = await CliCommandProcessor.ExecuteAsync(["edit", "R:", "--capacity-mb", "1"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("Cannot reduce capacity: current usage exceeds the requested capacity.", outcome.Message);
    }

    [Fact]
    public async Task SnapshotDiff_NormalizesDriveLetterAndIndex()
    {
        var controller = new FakeCliDiskController
        {
            DiffSuccess = true,
            Diff = new CliSnapshotDiff(["\\new.txt"], ["\\old.txt"], ["\\changed.txt"], [], [], 3),
        };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "diff", "r", "2"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastMountPoint);
        Assert.Equal(2, controller.LastDiffIndex);
        Assert.Contains("+ \\new.txt", outcome.Message);
        Assert.Contains("- \\old.txt", outcome.Message);
        Assert.Contains("~ \\changed.txt", outcome.Message);
        Assert.Contains("3 unchanged file(s).", outcome.Message);
    }

    [Fact]
    public async Task SnapshotDiff_NoDifferences_ReportsNoDifferences()
    {
        var controller = new FakeCliDiskController
        {
            DiffSuccess = true,
            Diff = new CliSnapshotDiff([], [], [], [], [], 5),
        };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "diff", "R:", "1"], controller);

        Assert.True(outcome.Success);
        Assert.Contains("No differences.", outcome.Message);
    }

    [Fact]
    public async Task SnapshotDiff_NotMounted_ReturnsNotMountedMessage()
    {
        var controller = new FakeCliDiskController { DiffSuccess = false, DiffMessage = string.Empty };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "diff", "Z:", "1"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("Z:", outcome.Message);
    }

    private sealed class FakeCliDiskController : ICliDiskController
    {
        public string? LastMountPoint
        {
            get; private set;
        }

        public ulong? LastCapacityBytes
        {
            get; private set;
        }

        public string? LastVolumeLabel
        {
            get; private set;
        }

        public uint? LastAutoSaveIntervalMinutes
        {
            get; private set;
        }

        public bool LastDisableAutoSave
        {
            get; private set;
        }

        public bool EditSuccess { get; set; } = true;

        public string EditMessage { get; set; } = string.Empty;

        public Task<(bool Success, string Message)> EditAsync(string mountPoint, ulong? capacityBytes, string? volumeLabel, uint? autoSaveIntervalMinutes, bool disableAutoSave)
        {
            LastMountPoint = mountPoint;
            LastCapacityBytes = capacityBytes;
            LastVolumeLabel = volumeLabel;
            LastAutoSaveIntervalMinutes = autoSaveIntervalMinutes;
            LastDisableAutoSave = disableAutoSave;
            return Task.FromResult((EditSuccess, EditMessage));
        }

        public int? LastDiffIndex
        {
            get; private set;
        }

        public bool DiffSuccess { get; set; } = true;

        public string DiffMessage { get; set; } = string.Empty;

        public CliSnapshotDiff? Diff { get; set; }

        public Task<(bool Success, string Message, CliSnapshotDiff? Diff)> DiffSnapshotAsync(string mountPoint, int index)
        {
            LastMountPoint = mountPoint;
            LastDiffIndex = index;
            return Task.FromResult<(bool, string, CliSnapshotDiff?)>((DiffSuccess, DiffMessage, DiffSuccess ? Diff : null));
        }

        public Task<(bool Success, string Message)> CreateAsync(string mountPoint, ulong capacityBytes, string? volumeLabel, string? imagePath, string? password) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message, IReadOnlyList<CliFileEntry>? Entries)> ListFilesAsync(string mountPoint, string? path) =>
            Task.FromResult<(bool, string, IReadOnlyList<CliFileEntry>?)>((false, string.Empty, null));

        public Task<(bool Success, string Message)> CloneAsync(string sourceMountPoint, string targetMountPoint) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> CreateSnapshotAsync(string mountPoint) =>
            Task.FromResult((false, string.Empty));

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
