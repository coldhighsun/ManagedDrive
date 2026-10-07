using System.Text.Json;
using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliCommandProcessorUsageTests
{
    private static readonly CliSpaceUsage Usage = new(
        "R:", CapacityBytes: 64UL * 1024 * 1024, UsedBytes: 4UL * 1024 * 1024, LogicalBytes: 3UL * 1024 * 1024,
        FileCount: 12, DirectoryCount: 3, LinkCount: 1, StreamCount: 2,
        TopDirectories: [new("\\Cache", 3UL * 1024 * 1024, 2UL * 1024 * 1024, 8), new("\\Src", 1024 * 1024, 1024 * 1024, 4)],
        TopFiles: [new("\\Cache\\big.bin", 2UL * 1024 * 1024, 2UL * 1024 * 1024, 1)],
        TopExtensions: [new(".bin", 5, 3UL * 1024 * 1024, 2UL * 1024 * 1024), new("(none)", 1, 512, 100)]);

    [Fact]
    public async Task Usage_NormalizesDriveLetterAndUsesTheDefaultTop()
    {
        var controller = new Controller { Result = (true, string.Empty, Usage) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["usage", "r"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastMountPoint);
        Assert.Equal(10, controller.LastTop);
    }

    [Fact]
    public async Task Usage_PrintsSummaryAndAllThreeLists()
    {
        var controller = new Controller { Result = (true, string.Empty, Usage) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["usage", "R:"], controller);

        Assert.Contains("12 files, 3 directories, 1 links, 2 streams", outcome.Message);
        Assert.Contains("Directories (", outcome.Message);
        Assert.Contains("\\Cache\\big.bin", outcome.Message);
        Assert.Contains("Extensions (", outcome.Message);
        Assert.Contains("75%", outcome.Message);
        Assert.Contains(".bin", outcome.Message);
    }

    [Theory]
    [InlineData("dir", "Directories (", new[] { "Files (", "Extensions (" })]
    [InlineData("file", "Files (", new[] { "Directories (", "Extensions (" })]
    [InlineData("EXT", "Extensions (", new[] { "Directories (", "Files (" })]
    public async Task Usage_By_PrintsOnlyThatList(string by, string expected, string[] absent)
    {
        var controller = new Controller { Result = (true, string.Empty, Usage) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["usage", "R:", "--by", by], controller);

        Assert.Contains(expected, outcome.Message);
        Assert.All(absent, heading => Assert.DoesNotContain(heading, outcome.Message));
    }

    [Fact]
    public async Task Usage_Top_IsPassedOn()
    {
        var controller = new Controller { Result = (true, string.Empty, Usage) };

        await CliCommandProcessor.ExecuteAsync(["usage", "R:", "--top", "25"], controller);

        Assert.Equal(25, controller.LastTop);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1001")]
    public async Task Usage_TopOutOfRange_FailsWithoutAskingTheController(string top)
    {
        var controller = new Controller { Result = (true, string.Empty, Usage) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["usage", "R:", "--top", top], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("--top", outcome.Message);
        Assert.Null(controller.LastMountPoint);
    }

    [Fact]
    public async Task Usage_UnknownBy_Fails()
    {
        var controller = new Controller { Result = (true, string.Empty, Usage) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["usage", "R:", "--by", "size"], controller);

        Assert.False(outcome.Success);
        Assert.Contains("--by", outcome.Message);
        Assert.Null(controller.LastMountPoint);
    }

    [Fact]
    public async Task Usage_NotMounted_ReturnsNotMountedMessage()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["usage", "Z:"], new Controller());

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("No disk is currently mounted at Z:.", outcome.Message);
    }

    [Fact]
    public async Task Usage_ControllerMessage_IsShown()
    {
        var controller = new Controller { Result = (false, "analysis failed", null) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["usage", "R:"], controller);

        Assert.Equal("analysis failed", outcome.Message);
    }

    [Fact]
    public async Task Usage_EmptyDisk_DoesNotDivideByZero()
    {
        var empty = new CliSpaceUsage("R:", 1024, 0, 0, 0, 0, 0, 0, [], [], []);
        var controller = new Controller { Result = (true, string.Empty, empty) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["usage", "R:"], controller);

        Assert.True(outcome.Success);
        Assert.Contains("0 files", outcome.Message);
    }

    [Fact]
    public async Task Usage_Json_PrintsEverythingEvenWithBy()
    {
        var controller = new Controller { Result = (true, string.Empty, Usage) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["usage", "R:", "--by", "dir", "--json"], controller);

        Assert.True(outcome.Json);
        using var document = JsonDocument.Parse(outcome.Message);
        var root = document.RootElement;
        Assert.Equal("R:", root.GetProperty("MountPoint").GetString());
        Assert.Equal(12, root.GetProperty("FileCount").GetInt32());
        Assert.Equal("\\Cache", root.GetProperty("TopDirectories")[0].GetProperty("Path").GetString());
        Assert.Equal(1, root.GetProperty("TopFiles").GetArrayLength());
        Assert.Equal(".bin", root.GetProperty("TopExtensions")[0].GetProperty("Extension").GetString());
    }

    [Fact]
    public async Task Usage_JsonNotMounted_PrintsFailureObject()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["usage", "Z:", "--json"], new Controller());

        Assert.True(outcome.Json);
        Assert.False(outcome.Success);
        using var document = JsonDocument.Parse(outcome.Message);
        Assert.False(document.RootElement.GetProperty("Success").GetBoolean());
    }

    private sealed class Controller : StubCliDiskController
    {
        public string? LastMountPoint { get; private set; }

        public int LastTop { get; private set; }

        public (bool Success, string Message, CliSpaceUsage? Usage) Result { get; set; } = (false, string.Empty, null);

        public override Task<(bool Success, string Message, CliSpaceUsage? Usage)> GetSpaceUsageAsync(string mountPoint, int top)
        {
            LastMountPoint = mountPoint;
            LastTop = top;
            return Task.FromResult(Result);
        }
    }
}
