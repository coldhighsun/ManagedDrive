using System.Text.Json;
using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliCommandProcessorInfoTests
{
    private static readonly CliDiskDetails Details = new(
        "R:", "Scratch", 1024UL * 1024, 64UL * 1024 * 1024, ReadOnly: false,
        ImagePath: @"C:\disks\r.mdr", SourceArchivePath: null, PasswordProtected: true,
        AutoSaveIntervalMinutes: 5, CompressionLevel: "Fastest",
        MaxSnapshotCount: 10, MaxSnapshotSizeBytes: null, SnapshotCount: 3, LastSaveTime: null);

    [Fact]
    public async Task Info_NormalizesDriveLetterAndPrintsTheDetails()
    {
        var controller = new Controller { Result = (true, string.Empty, Details) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["info", "r"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("R:", controller.LastMountPoint);
        Assert.Contains("Scratch", outcome.Message);
        Assert.Contains(@"C:\disks\r.mdr", outcome.Message);
        Assert.Contains("Encrypted:", outcome.Message);
        Assert.Contains("every 5 min", outcome.Message);
        Assert.Contains("3 kept (at most 10)", outcome.Message);
        Assert.Contains("(not saved yet)", outcome.Message);
    }

    [Fact]
    public async Task Info_MemoryOnlyDiskWithoutSnapshots_SaysSo()
    {
        var memoryOnly = Details with
        {
            ImagePath = null,
            PasswordProtected = false,
            AutoSaveIntervalMinutes = null,
            MaxSnapshotCount = null,
            SnapshotCount = null,
        };
        var controller = new Controller { Result = (true, string.Empty, memoryOnly) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["info", "R:"], controller);

        Assert.Contains("(memory only)", outcome.Message);
        Assert.Contains("not enabled", outcome.Message);
        Assert.Contains("off", outcome.Message);
    }

    [Fact]
    public async Task Info_NotMounted_ReturnsNotMountedMessage()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["info", "Z:"], new Controller());

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("No disk is currently mounted at Z:.", outcome.Message);
    }

    [Fact]
    public async Task Info_Json_PrintsTheDetailsObject()
    {
        var controller = new Controller { Result = (true, string.Empty, Details) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["info", "R:", "--json"], controller);

        Assert.True(outcome.Json);
        using var document = JsonDocument.Parse(outcome.Message);
        var root = document.RootElement;
        Assert.Equal("R:", root.GetProperty("MountPoint").GetString());
        Assert.Equal(64UL * 1024 * 1024, root.GetProperty("TotalBytes").GetUInt64());
        Assert.True(root.GetProperty("PasswordProtected").GetBoolean());
        Assert.Equal(3, root.GetProperty("SnapshotCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("SourceArchivePath").ValueKind);
        Assert.Equal("Fastest", root.GetProperty("CompressionLevel").GetString());
    }

    [Fact]
    public async Task Info_JsonNotMounted_PrintsFailureObject()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["info", "Z:", "--json"], new Controller());

        Assert.True(outcome.Json);
        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        using var document = JsonDocument.Parse(outcome.Message);
        Assert.False(document.RootElement.GetProperty("Success").GetBoolean());
    }

    private sealed class Controller : StubCliDiskController
    {
        public string? LastMountPoint { get; private set; }

        public (bool Success, string Message, CliDiskDetails? Details) Result { get; set; } = (false, string.Empty, null);

        public override Task<(bool Success, string Message, CliDiskDetails? Details)> GetDiskInfoAsync(string mountPoint)
        {
            LastMountPoint = mountPoint;
            return Task.FromResult(Result);
        }
    }
}
