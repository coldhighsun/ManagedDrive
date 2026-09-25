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
    public async Task Mount_RelativeImagePathWithWorkingDirectory_ResolvesAgainstWorkingDirectory()
    {
        var imagePath = CreateTempImageFile();
        try
        {
            var controller = new FakeCliDiskController { MountSuccess = true };

            var outcome = await CliCommandProcessor.ExecuteAsync(
                ["mount", Path.GetFileName(imagePath), "R:"],
                controller,
                Path.GetDirectoryName(imagePath));

            Assert.True(outcome.Success);
            Assert.Equal(imagePath, controller.LastImagePath);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task Mount_RelativePasswordFileWithWorkingDirectory_ReadsFileFromWorkingDirectory()
    {
        var imagePath = CreateTempImageFile();
        var passwordFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.txt");
        File.WriteAllText(passwordFile, "secret");
        try
        {
            var controller = new FakeCliDiskController { MountSuccess = true };

            var outcome = await CliCommandProcessor.ExecuteAsync(
                ["mount", imagePath, "R:", "--password-file", Path.GetFileName(passwordFile)],
                controller,
                Path.GetDirectoryName(passwordFile));

            Assert.True(outcome.Success);
            Assert.Equal("secret", controller.LastOverrides?.Password);
        }
        finally
        {
            File.Delete(imagePath);
            File.Delete(passwordFile);
        }
    }

    [Theory]
    [InlineData("disk.mdr", null, "disk.mdr")]
    [InlineData("disk.mdr", "relative\\dir", "disk.mdr")]
    [InlineData("C:\\images\\disk.mdr", "D:\\work", "C:\\images\\disk.mdr")]
    [InlineData("..\\disk.mdr", "D:\\work\\sub", "D:\\work\\disk.mdr")]
    [InlineData("\\images\\disk.mdr", "D:\\work", "D:\\images\\disk.mdr")]
    [InlineData("", "D:\\work", "")]
    [InlineData("   ", "D:\\work", "   ")]
    public void ResolvePath_VariousInputs_ResolvesOnlyRelativePathsAgainstFullyQualifiedWorkingDirectory(
        string path, string? workingDirectory, string expected)
    {
        var resolved = CliCommandProcessor.ResolvePath(path, workingDirectory);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public async Task Export_RelativeOutputPathWithWorkingDirectory_ResolvesAgainstWorkingDirectory()
    {
        var controller = new FakeCliDiskController { ExportSuccess = true };

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["export", "R:", "out\\disk.mdr"],
            controller,
            "D:\\work");

        Assert.True(outcome.Success);
        Assert.Equal("D:\\work\\out\\disk.mdr", controller.LastExportOutputPath);
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
            Disks = [new("R:", "MyDisk", 1024, 4096)],
        };

        var outcome = await CliCommandProcessor.ExecuteAsync(["list"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Single(outcome.Disks!);
        Assert.Equal("R:", outcome.Disks![0].MountPoint);
    }

    [Fact]
    public async Task Export_ForwardsPathFormatCompressionPassword_ToController()
    {
        var controller = new FakeCliDiskController { ExportSuccess = true, ExportMessage = "Exported." };

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["export", "r", "out.zip", "--format", "Zip", "--compression", "SmallestSize"],
            controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastMountPoint);
        Assert.Equal("out.zip", controller.LastExportOutputPath);
        Assert.Equal(Cli.Core.ArchiveExportFormat.Zip, controller.LastExportFormat);
        Assert.Equal(Cli.Core.ImageCompressionLevel.SmallestSize, controller.LastExportCompressionLevel);
        Assert.Null(controller.LastExportPassword);
    }

    [Fact]
    public async Task Export_WithPassword_ForwardsPasswordAndNoFormat()
    {
        var controller = new FakeCliDiskController { ExportSuccess = true };

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["export", "R:", "out.mdr", "--password", "secret"],
            controller);

        Assert.True(outcome.Success);
        Assert.Null(controller.LastExportFormat);
        Assert.Equal("secret", controller.LastExportPassword);
    }

    [Fact]
    public async Task Export_PasswordWithFormat_ReturnsErrorWithoutCallingController()
    {
        var controller = new FakeCliDiskController();

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["export", "R:", "out.zip", "--format", "Zip", "--password", "secret"],
            controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Null(controller.LastExportOutputPath);
    }

    [Fact]
    public async Task Export_PasswordAndPasswordFile_ReturnsErrorWithoutCallingController()
    {
        var controller = new FakeCliDiskController();

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["export", "R:", "out.mdr", "--password", "a", "--password-file", "b.txt"],
            controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Null(controller.LastExportOutputPath);
    }

    /// <summary>
    /// Without <c>--force</c>, an existing output file is reported instead of being overwritten.
    /// </summary>
    [Fact]
    public async Task Export_OutputFileExistsWithoutForce_ReturnsErrorWithoutCallingController()
    {
        var outputPath = CreateTempImageFile();
        try
        {
            var controller = new FakeCliDiskController();

            var outcome = await CliCommandProcessor.ExecuteAsync(["export", "R:", outputPath], controller);

            Assert.False(outcome.Success);
            Assert.Equal(1, outcome.ExitCode);
            Assert.Contains("--force", outcome.Message);
            Assert.Null(controller.LastExportOutputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    /// <summary>
    /// <c>--force</c> and its <c>-f</c> alias let the export overwrite an existing output file.
    /// </summary>
    /// <param name="forceOption">The spelling of the force option to pass.</param>
    [Theory]
    [InlineData("--force")]
    [InlineData("-f")]
    public async Task Export_OutputFileExistsWithForce_ForwardsToController(string forceOption)
    {
        var outputPath = CreateTempImageFile();
        try
        {
            var controller = new FakeCliDiskController { ExportSuccess = true };

            var outcome = await CliCommandProcessor.ExecuteAsync(["export", "R:", outputPath, forceOption], controller);

            Assert.True(outcome.Success);
            Assert.Equal(outputPath, controller.LastExportOutputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Export_ControllerReturnsEmptyMessage_ReturnsNotMountedMessage()
    {
        var controller = new FakeCliDiskController { ExportSuccess = false, ExportMessage = string.Empty };

        var outcome = await CliCommandProcessor.ExecuteAsync(["export", "R:", "out.mdr"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("No disk is currently mounted at R:.", outcome.Message);
    }

    [Fact]
    public async Task List_Json_ReturnsOutcomeWithJsonFlagSet()
    {
        var controller = new FakeCliDiskController
        {
            Disks = [new("R:", "MyDisk", 1024, 4096)],
        };

        var outcome = await CliCommandProcessor.ExecuteAsync(["list", "--json"], controller);

        Assert.True(outcome.Success);
        Assert.True(outcome.Json);
        Assert.Single(outcome.Disks!);
    }

    [Fact]
    public async Task List_WithoutJson_ReturnsOutcomeWithJsonFlagUnset()
    {
        var controller = new FakeCliDiskController();

        var outcome = await CliCommandProcessor.ExecuteAsync(["list"], controller);

        Assert.False(outcome.Json);
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

        public string? LastExportOutputPath
        {
            get; private set;
        }

        public ManagedDrive.Cli.Core.ArchiveExportFormat? LastExportFormat
        {
            get; private set;
        }

        public ManagedDrive.Cli.Core.ImageCompressionLevel? LastExportCompressionLevel
        {
            get; private set;
        }

        public string? LastExportPassword
        {
            get; private set;
        }

        public bool ExportSuccess { get; set; } = true;

        public string ExportMessage { get; set; } = string.Empty;

        public Task<(bool Success, string Message)> ExportAsync(string mountPoint, string outputPath, ManagedDrive.Cli.Core.ArchiveExportFormat? archiveFormat, ManagedDrive.Cli.Core.ImageCompressionLevel compressionLevel, string? password)
        {
            LastMountPoint = mountPoint;
            LastExportOutputPath = outputPath;
            LastExportFormat = archiveFormat;
            LastExportCompressionLevel = compressionLevel;
            LastExportPassword = password;
            return Task.FromResult((ExportSuccess, ExportMessage));
        }

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

        public Task<(bool Success, string Message)> SetPasswordAsync(string mountPoint, string? newPassword) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> CreateAsync(string mountPoint, ulong capacityBytes, string? volumeLabel, string? imagePath, string? password) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message, IReadOnlyList<CliFileEntry>? Entries)> ListFilesAsync(string mountPoint, string? path) =>
            Task.FromResult<(bool, string, IReadOnlyList<CliFileEntry>?)>((false, string.Empty, null));

        public Task<(bool Success, string Message)> CloneAsync(string sourceMountPoint, string targetMountPoint) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message, CliSnapshotDiff? Diff)> DiffSnapshotAsync(string mountPoint, int index) =>
            Task.FromResult<(bool, string, CliSnapshotDiff?)>((false, string.Empty, null));

        public Task<(bool Success, string Message)> EditAsync(string mountPoint, ulong? capacityBytes, string? volumeLabel, uint? autoSaveIntervalMinutes, bool disableAutoSave) =>
            Task.FromResult((false, string.Empty));

        public Task<(bool Success, string Message)> CreateSnapshotAsync(string mountPoint) =>
            Task.FromResult((false, string.Empty));

        public Task<bool> UnmountAsync(string mountPoint, bool deleteImage)
        {
            LastMountPoint = mountPoint;
            LastDeleteImage = deleteImage;
            return Task.FromResult(UnmountSuccess);
        }
    }
}
