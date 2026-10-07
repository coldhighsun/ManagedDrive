using System.Text.Json;
using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliCommandProcessorSnapshotExtractTests
{
    [Fact]
    public async Task Extract_ForwardsEverythingAndNormalizesTheDriveLetter()
    {
        var controller = new Controller { Result = (true, "Extracted 1 file(s).") };

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["snapshot", "extract", "r", "2", "\\Folder\\a.txt", @"C:\out\a.txt"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("Extracted 1 file(s).", outcome.Message);
        Assert.Equal("R:", controller.MountPoint);
        Assert.Equal(2, controller.Index);
        Assert.Equal("\\Folder\\a.txt", controller.SnapshotPath);
        Assert.Equal(@"C:\out\a.txt", controller.OutputPath);
        Assert.False(controller.Overwrite);
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("-f")]
    public async Task Extract_ForceOption_AllowsOverwriting(string option)
    {
        var controller = new Controller { Result = (true, "ok") };

        await CliCommandProcessor.ExecuteAsync(
            ["snapshot", "extract", "R:", "1", "\\a.txt", @"C:\out\a.txt", option], controller);

        Assert.True(controller.Overwrite);
    }

    [Fact]
    public async Task Extract_RelativeOutput_IsResolvedAgainstTheCallersWorkingDirectory()
    {
        var controller = new Controller { Result = (true, "ok") };
        var workingDirectory = Path.Combine(Path.GetTempPath(), "caller");

        await CliCommandProcessor.ExecuteAsync(
            ["snapshot", "extract", "R:", "1", "\\a.txt", "out.txt"], controller, workingDirectory);

        Assert.Equal(Path.Combine(workingDirectory, "out.txt"), controller.OutputPath);
    }

    [Fact]
    public async Task Extract_NotMounted_ReturnsNotMountedMessage()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["snapshot", "extract", "Z:", "1", "\\a.txt", @"C:\out"], new Controller());

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("No disk is currently mounted at Z:.", outcome.Message);
    }

    [Fact]
    public async Task Extract_Failure_ReturnsTheReasonAndNonZeroExitCode()
    {
        var controller = new Controller { Result = (false, "'C:\\out\\a.txt' already exists. Re-run with --force to overwrite.") };

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["snapshot", "extract", "R:", "1", "\\a.txt", @"C:\out\a.txt"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("--force", outcome.Message);
    }

    [Fact]
    public async Task Extract_Json_PrintsTheResultAsJson()
    {
        var controller = new Controller { Result = (true, "Extracted 1 file(s).") };

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["snapshot", "extract", "R:", "1", "\\a.txt", @"C:\out\a.txt", "--json"], controller);

        Assert.True(outcome.Json);
        using var document = JsonDocument.Parse(outcome.Message);
        Assert.True(document.RootElement.GetProperty("Success").GetBoolean());
    }

    [Fact]
    public async Task Extract_MissingArguments_DoesNotCallTheController()
    {
        var controller = new Controller { Result = (true, "ok") };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "extract", "R:", "1"], controller);

        Assert.False(outcome.Success);
        Assert.False(controller.Called);
    }

    private sealed class Controller : StubCliDiskController
    {
        public bool Called { get; private set; }

        public string? MountPoint { get; private set; }

        public int Index { get; private set; }

        public string? SnapshotPath { get; private set; }

        public string? OutputPath { get; private set; }

        public bool Overwrite { get; private set; }

        public (bool Success, string Message) Result { get; set; } = (false, string.Empty);

        public override Task<(bool Success, string Message)> ExtractSnapshotAsync(
            string mountPoint, int index, string snapshotPath, string outputPath, bool overwrite)
        {
            Called = true;
            MountPoint = mountPoint;
            Index = index;
            SnapshotPath = snapshotPath;
            OutputPath = outputPath;
            Overwrite = overwrite;
            return Task.FromResult(Result);
        }
    }
}
