using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliCommandProcessorSetPasswordTests
{
    [Fact]
    public async Task SetPassword_WithPasswordOption_ForwardsPasswordAndNormalizesDriveLetter()
    {
        var controller = new FakeCliDiskController { SetPasswordSuccess = true, SetPasswordMessage = "Password set for R:." };

        var outcome = await CliCommandProcessor.ExecuteAsync(["set-password", "r", "--password", "hunter2"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastMountPoint);
        Assert.Equal("hunter2", controller.LastPassword);
    }

    [Fact]
    public async Task SetPassword_WithRemoveOption_ForwardsNullPassword()
    {
        var controller = new FakeCliDiskController { SetPasswordSuccess = true };

        var outcome = await CliCommandProcessor.ExecuteAsync(["set-password", "R:", "--remove"], controller);

        Assert.True(outcome.Success);
        Assert.Null(controller.LastPassword);
        Assert.True(controller.SetPasswordCalled);
    }

    [Fact]
    public async Task SetPassword_WithPasswordFile_ReadsFirstLine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.txt");
        File.WriteAllText(path, "filepassword\nsecondline");
        try
        {
            var controller = new FakeCliDiskController { SetPasswordSuccess = true };

            var outcome = await CliCommandProcessor.ExecuteAsync(["set-password", "R:", "--password-file", path], controller);

            Assert.True(outcome.Success);
            Assert.Equal("filepassword", controller.LastPassword);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SetPassword_NoOptionSpecified_FailsWithoutCallingController()
    {
        var controller = new FakeCliDiskController { SetPasswordSuccess = true };

        var outcome = await CliCommandProcessor.ExecuteAsync(["set-password", "R:"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.False(controller.SetPasswordCalled);
    }

    [Fact]
    public async Task SetPassword_MultipleOptionsSpecified_FailsWithoutCallingController()
    {
        var controller = new FakeCliDiskController { SetPasswordSuccess = true };

        var outcome = await CliCommandProcessor.ExecuteAsync(["set-password", "R:", "--password", "a", "--remove"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.False(controller.SetPasswordCalled);
    }

    [Fact]
    public async Task SetPassword_NotMounted_ReturnsNotMountedMessage()
    {
        var controller = new FakeCliDiskController { SetPasswordSuccess = false, SetPasswordMessage = string.Empty };

        var outcome = await CliCommandProcessor.ExecuteAsync(["set-password", "Z:", "--remove"], controller);

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

        public string? LastPassword
        {
            get; private set;
        }

        public bool SetPasswordCalled
        {
            get; private set;
        }

        public bool SetPasswordSuccess { get; set; } = true;

        public string SetPasswordMessage { get; set; } = string.Empty;

        public Task<(bool Success, string Message)> SetPasswordAsync(string mountPoint, string? newPassword)
        {
            LastMountPoint = mountPoint;
            LastPassword = newPassword;
            SetPasswordCalled = true;
            return Task.FromResult((SetPasswordSuccess, SetPasswordMessage));
        }

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

        public Task<bool> UnmountAsync(string mountPoint, bool deleteImage) => Task.FromResult(false);

        public Task<(bool Success, string Message)> CreateAsync(string mountPoint, ulong capacityBytes, string? volumeLabel, string? imagePath, string? password) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message, IReadOnlyList<CliFileEntry>? Entries)> ListFilesAsync(string mountPoint, string? path) =>
            Task.FromResult<(bool, string, IReadOnlyList<CliFileEntry>?)>((false, string.Empty, null));

        public Task<(bool Success, string Message)> CloneAsync(string sourceMountPoint, string targetMountPoint) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> CreateSnapshotAsync(string mountPoint) =>
            Task.FromResult((false, string.Empty));
    }
}
