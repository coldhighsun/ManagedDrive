using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliCommandProcessorCreateAndLsTests
{
    [Fact]
    public async Task Create_ForwardsCapacityLabelImageAndNormalizesDriveLetter()
    {
        var controller = new FakeCliDiskController { CreateSuccess = true, CreateMessage = "Mounted R:." };

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["create", "r", "--capacity-mb", "256", "--label", "MyDisk", "--image", @"C:\disks\r.mdr"],
            controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastMountPoint);
        Assert.Equal(256UL * 1024 * 1024, controller.LastCapacityBytes);
        Assert.Equal("MyDisk", controller.LastVolumeLabel);
        Assert.Equal(@"C:\disks\r.mdr", controller.LastImagePath);
    }

    [Fact]
    public async Task Create_WithPasswordFile_ReadsFirstLineAndRequiresImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.txt");
        File.WriteAllText(path, "filepassword\nsecondline");
        try
        {
            var controller = new FakeCliDiskController { CreateSuccess = true };

            var outcome = await CliCommandProcessor.ExecuteAsync(
                ["create", "R:", "--capacity-mb", "128", "--image", @"C:\disks\r.mdr", "--password-file", path],
                controller);

            Assert.True(outcome.Success);
            Assert.Equal("filepassword", controller.LastPassword);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Create_PasswordWithoutImage_FailsWithoutCallingController()
    {
        var controller = new FakeCliDiskController { CreateSuccess = true };

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["create", "R:", "--capacity-mb", "128", "--password", "secret"],
            controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.False(controller.CreateCalled);
    }

    [Fact]
    public async Task Create_Failure_ReturnsErrorMessageAndNonZeroExitCode()
    {
        var controller = new FakeCliDiskController { CreateSuccess = false, CreateMessage = "R: is already mounted." };

        var outcome = await CliCommandProcessor.ExecuteAsync(["create", "R:", "--capacity-mb", "128"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("R: is already mounted.", outcome.Message);
    }

    [Fact]
    public async Task Ls_NormalizesDriveLetterAndForwardsPath()
    {
        var controller = new FakeCliDiskController
        {
            LsSuccess = true,
            LsEntries = [new CliFileEntry("Sub", true, 0), new CliFileEntry("file.txt", false, 42)],
        };

        var outcome = await CliCommandProcessor.ExecuteAsync(["ls", "r", "\\Folder"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastMountPoint);
        Assert.Equal("\\Folder", controller.LastLsPath);
        Assert.Contains("Sub/", outcome.Message);
        Assert.Contains("file.txt\t42", outcome.Message);
    }

    [Fact]
    public async Task Ls_NotMounted_ReturnsNotMountedMessage()
    {
        var controller = new FakeCliDiskController { LsSuccess = false, LsMessage = string.Empty };

        var outcome = await CliCommandProcessor.ExecuteAsync(["ls", "Z:"], controller);

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

        public ulong LastCapacityBytes
        {
            get; private set;
        }

        public string? LastVolumeLabel
        {
            get; private set;
        }

        public string? LastImagePath
        {
            get; private set;
        }

        public string? LastPassword
        {
            get; private set;
        }

        public bool CreateCalled
        {
            get; private set;
        }

        public bool CreateSuccess { get; set; } = true;

        public string CreateMessage { get; set; } = string.Empty;

        public Task<(bool Success, string Message)> CreateAsync(string mountPoint, ulong capacityBytes, string? volumeLabel, string? imagePath, string? password)
        {
            LastMountPoint = mountPoint;
            LastCapacityBytes = capacityBytes;
            LastVolumeLabel = volumeLabel;
            LastImagePath = imagePath;
            LastPassword = password;
            CreateCalled = true;
            return Task.FromResult((CreateSuccess, CreateMessage));
        }

        public string? LastLsPath
        {
            get; private set;
        }

        public bool LsSuccess { get; set; } = true;

        public string LsMessage { get; set; } = string.Empty;

        public IReadOnlyList<CliFileEntry> LsEntries { get; set; } = [];

        public Task<(bool Success, string Message, IReadOnlyList<CliFileEntry>? Entries)> ListFilesAsync(string mountPoint, string? path)
        {
            LastMountPoint = mountPoint;
            LastLsPath = path;
            return Task.FromResult<(bool, string, IReadOnlyList<CliFileEntry>?)>(
                LsSuccess ? (true, LsMessage, LsEntries) : (false, LsMessage, null));
        }

        public Task<(bool Success, string Message)> CloneAsync(string sourceMountPoint, string targetMountPoint) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message, CliSnapshotDiff? Diff)> DiffSnapshotAsync(string mountPoint, int index) =>
            Task.FromResult<(bool, string, CliSnapshotDiff?)>((false, string.Empty, null));

        public Task<(bool Success, string Message)> EditAsync(string mountPoint, ulong? capacityBytes, string? volumeLabel, uint? autoSaveIntervalMinutes, bool disableAutoSave) =>
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
