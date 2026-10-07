using System.Text.Json;
using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliCommandProcessorJsonTests
{
    [Fact]
    public async Task Save_Json_PrintsSuccessAndMessageAsJson()
    {
        var controller = new Controller { SaveResult = (true, "Saved R:.") };

        var outcome = await CliCommandProcessor.ExecuteAsync(["save", "R:", "--json"], controller);

        Assert.True(outcome.Json);
        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        using var document = JsonDocument.Parse(outcome.Message);
        Assert.True(document.RootElement.GetProperty("Success").GetBoolean());
        Assert.Equal("Saved R:.", document.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task Save_JsonNotMounted_PrintsFailureAndKeepsExitCode()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["save", "R:", "--json"], new Controller());

        Assert.True(outcome.Json);
        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        using var document = JsonDocument.Parse(outcome.Message);
        Assert.False(document.RootElement.GetProperty("Success").GetBoolean());
        Assert.Equal("No disk is currently mounted at R:.", document.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task Save_JsonBeforeTheSubcommand_IsAccepted()
    {
        var controller = new Controller { SaveResult = (true, "Saved R:.") };

        var outcome = await CliCommandProcessor.ExecuteAsync(["--json", "save", "R:"], controller);

        Assert.True(outcome.Json);
        Assert.True(outcome.Success);
    }

    [Fact]
    public async Task Save_WithoutJson_StaysPlainText()
    {
        var controller = new Controller { SaveResult = (true, "Saved R:.") };

        var outcome = await CliCommandProcessor.ExecuteAsync(["save", "R:"], controller);

        Assert.False(outcome.Json);
        Assert.Equal("Saved R:.", outcome.Message);
    }

    [Fact]
    public async Task Format_JsonWithoutYes_PrintsTheRefusalAsJson()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["format", "R:", "--json"], new Controller());

        Assert.True(outcome.Json);
        Assert.False(outcome.Success);
        using var document = JsonDocument.Parse(outcome.Message);
        Assert.Contains("--yes", document.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task Unmount_Json_PrintsSuccessAsJson()
    {
        var controller = new Controller { UnmountResult = (true, null) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["unmount", "R:", "--json"], controller);

        Assert.True(outcome.Json);
        Assert.True(outcome.Success);
        using var document = JsonDocument.Parse(outcome.Message);
        Assert.Equal("Unmounted R:.", document.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task Ls_Json_PrintsTheEntriesAsAnArray()
    {
        var controller = new Controller { LsResult = (true, string.Empty, [new("Sub", true, 0), new("a \"b\".txt", false, 42)]) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["ls", "R:", "--json"], controller);

        Assert.True(outcome.Json);
        Assert.True(outcome.Success);
        using var document = JsonDocument.Parse(outcome.Message);
        var entries = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("Sub", entries[0].GetProperty("Name").GetString());
        Assert.True(entries[0].GetProperty("IsDirectory").GetBoolean());
        Assert.Equal("a \"b\".txt", entries[1].GetProperty("Name").GetString());
        Assert.Equal(42UL, entries[1].GetProperty("SizeBytes").GetUInt64());
    }

    [Fact]
    public async Task Ls_JsonEmptyDirectory_PrintsAnEmptyArray()
    {
        var controller = new Controller { LsResult = (true, string.Empty, []) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["ls", "R:", "--json"], controller);

        using var document = JsonDocument.Parse(outcome.Message);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(0, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Ls_JsonFailure_PrintsTheErrorAsJson()
    {
        var controller = new Controller { LsResult = (false, "Path not found.", null) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["ls", "R:", "\\nope", "--json"], controller);

        Assert.True(outcome.Json);
        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        using var document = JsonDocument.Parse(outcome.Message);
        Assert.Equal("Path not found.", document.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task SnapshotList_Json_PrintsTheSnapshotsAndDropsTheTextRendering()
    {
        var when = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var controller = new Controller { SnapshotListResult = (true, string.Empty, [new(1, when, 2048)]) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "list", "R:", "--json"], controller);

        Assert.True(outcome.Json);
        Assert.Null(outcome.Snapshots);
        using var document = JsonDocument.Parse(outcome.Message);
        var first = document.RootElement[0];
        Assert.Equal(1, first.GetProperty("Index").GetInt32());
        Assert.Equal(2048UL, first.GetProperty("SizeBytes").GetUInt64());
        Assert.Equal(when, first.GetProperty("TimestampUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task SnapshotList_WithoutJson_KeepsTheSnapshotsForTheTableRenderer()
    {
        var controller = new Controller { SnapshotListResult = (true, string.Empty, [new(1, DateTimeOffset.UnixEpoch, 1)]) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "list", "R:"], controller);

        Assert.False(outcome.Json);
        Assert.Single(outcome.Snapshots!);
    }

    [Fact]
    public async Task SnapshotDiff_Json_PrintsTheDiffObject()
    {
        var diff = new CliSnapshotDiff(["\\new.txt"], ["\\gone.txt"], ["\\changed.txt"], ["\\newdir"], [], 7);
        var controller = new Controller { DiffResult = (true, string.Empty, diff) };

        var outcome = await CliCommandProcessor.ExecuteAsync(["snapshot", "diff", "R:", "1", "--json"], controller);

        Assert.True(outcome.Json);
        using var document = JsonDocument.Parse(outcome.Message);
        var root = document.RootElement;
        Assert.Equal("\\new.txt", root.GetProperty("AddedFiles")[0].GetString());
        Assert.Equal("\\gone.txt", root.GetProperty("RemovedFiles")[0].GetString());
        Assert.Equal("\\changed.txt", root.GetProperty("ModifiedFiles")[0].GetString());
        Assert.Equal("\\newdir", root.GetProperty("AddedDirectories")[0].GetString());
        Assert.Equal(0, root.GetProperty("RemovedDirectories").GetArrayLength());
        Assert.Equal(7, root.GetProperty("UnchangedFileCount").GetInt32());
    }

    [Fact]
    public async Task Json_ValidationFailureInsideAHandler_IsPrintedAsJson()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["mount", "x.mdr", "R:", "--custom-zstd-level", "99", "--json"], new Controller());

        Assert.True(outcome.Json);
        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        using var document = JsonDocument.Parse(outcome.Message);
        Assert.Contains("--custom-zstd-level", document.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task Json_UnknownCommand_StaysPlainText()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["--json", "nosuchcommand"], new Controller());

        Assert.False(outcome.Json);
        Assert.False(outcome.Success);
        Assert.NotEqual(0, outcome.ExitCode);
    }

    [Fact]
    public async Task Exit_Json_PrintsSuccessAsJson()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["exit", "--json"], new Controller());

        Assert.True(outcome.Json);
        Assert.True(outcome.Success);
    }

    private sealed class Controller : StubCliDiskController
    {
        public (bool Success, string Message) SaveResult { get; set; } = (false, string.Empty);

        public (bool Unmounted, string? SaveError) UnmountResult { get; set; } = (false, null);

        public (bool Success, string Message, IReadOnlyList<CliFileEntry>? Entries) LsResult { get; set; } = (false, string.Empty, null);

        public (bool Success, string Message, IReadOnlyList<CliSnapshotInfo>? Snapshots) SnapshotListResult { get; set; } =
            (false, string.Empty, null);

        public (bool Success, string Message, CliSnapshotDiff? Diff) DiffResult { get; set; } = (false, string.Empty, null);

        public override Task<(bool Success, string Message)> SaveAsync(string mountPoint) => Task.FromResult(SaveResult);

        public override Task<(bool Unmounted, string? SaveError)> UnmountAsync(string mountPoint, bool deleteImage) =>
            Task.FromResult(UnmountResult);

        public override Task<(bool Success, string Message, IReadOnlyList<CliFileEntry>? Entries)> ListFilesAsync(string mountPoint, string? path) =>
            Task.FromResult(LsResult);

        public override Task<(bool Success, string Message, IReadOnlyList<CliSnapshotInfo>? Snapshots)> ListSnapshotsAsync(string mountPoint) =>
            Task.FromResult(SnapshotListResult);

        public override Task<(bool Success, string Message, CliSnapshotDiff? Diff)> DiffSnapshotAsync(string mountPoint, int index) =>
            Task.FromResult(DiffResult);
    }
}
