using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliWatchCommandTests
{
    [Theory]
    [InlineData(new[] { "watch" }, true, false)]
    [InlineData(new[] { "WATCH" }, true, false)]
    [InlineData(new[] { "watch", "--json" }, true, true)]
    [InlineData(new[] { "--json", "watch" }, true, true)]
    [InlineData(new[] { "watch", "--help" }, false, false)]
    [InlineData(new[] { "watch", "R:" }, false, false)]
    [InlineData(new[] { "watch", "watch" }, false, false)]
    [InlineData(new[] { "--json" }, false, false)]
    [InlineData(new[] { "list" }, false, false)]
    [InlineData(new string[0], false, false)]
    public void IsWatchCommand_ClassifiesTheArguments(string[] args, bool expected, bool expectedJson)
    {
        var result = CliCommandProcessor.IsWatchCommand(args, out var json);

        Assert.Equal(expected, result);
        Assert.Equal(expectedJson, json);
    }

    [Fact]
    public async Task Watch_RunThroughTheProcessor_ReportsThatItNeedsTheClient()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["watch"], new Controller());

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("watch", outcome.Message);
    }

    [Fact]
    public async Task Watch_Help_ListsTheEvents()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["watch", "--help"], new Controller());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("save-failed", outcome.Message);
    }

    [Fact]
    public async Task RootHelp_ListsWatchAndTheGlobalJsonOption()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["--help"], new Controller());

        Assert.Contains("watch", outcome.Message);
        Assert.Contains("--json", outcome.Message);
        Assert.Contains("info", outcome.Message);
    }

    [Fact]
    public void CliEvent_ToText_ShowsTimeNameMountPointAndDetail()
    {
        var when = new DateTimeOffset(2026, 10, 7, 12, 34, 56, TimeSpan.Zero);

        var text = new CliEvent(CliEventNames.SaveFailed, "R:", when, "disk full").ToText();

        Assert.Equal($"{when.ToLocalTime():HH:mm:ss} save-failed R: disk full", text);
    }

    [Fact]
    public void CliEvent_ToText_WithoutDetail_HasNoTrailingSpace()
    {
        var text = new CliEvent(CliEventNames.Unmounted, "R:", DateTimeOffset.Now).ToText();

        Assert.EndsWith("unmounted R:", text);
    }

    [Fact]
    public void CliEvent_ToText_MultiLineDetail_StaysOnOneLine()
    {
        var text = new CliEvent(CliEventNames.SaveFailed, "R:", DateTimeOffset.Now, "first\r\nsecond\nthird").ToText();

        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("first second third", text);
    }

    [Fact]
    public void CliEvent_ToJson_IsASingleLineWithEveryField()
    {
        var when = new DateTimeOffset(2026, 10, 7, 12, 34, 56, TimeSpan.Zero);

        var json = new CliEvent(CliEventNames.HighUsage, "R:", when, "91.2% used\r\nsecond line").ToJson();

        Assert.DoesNotContain('\n', json);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("high-usage", document.RootElement.GetProperty("Event").GetString());
        Assert.Equal("R:", document.RootElement.GetProperty("MountPoint").GetString());
        Assert.Equal(when, document.RootElement.GetProperty("Time").GetDateTimeOffset());
        Assert.Equal("91.2% used\r\nsecond line", document.RootElement.GetProperty("Message").GetString());
    }

    private sealed class Controller : StubCliDiskController
    {
    }
}
