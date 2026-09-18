using System.Text.Json;
using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliPipeProtocolTests
{
    [Fact]
    public void SerializeRequest_ThenDeserializeRequest_RoundTripsArgs()
    {
        string[] args = ["mount", "C:\\disks\\test.mdr", "R:", "--read-only"];

        var json = CliPipeProtocol.SerializeRequest(args);
        var roundTripped = CliPipeProtocol.DeserializeRequest(json);

        Assert.Equal(args, roundTripped);
    }

    [Fact]
    public void DeserializeRequest_NullJsonLiteral_ReturnsEmptyArray()
    {
        var result = CliPipeProtocol.DeserializeRequest("null");

        Assert.Empty(result);
    }

    [Fact]
    public void SerializeResponse_ThenDeserializeResponse_RoundTripsAllFields()
    {
        var response = new CliResponse(
            true,
            "Mounted R:.",
            [new("R:", "MyDisk", 1024, 4096)],
            0,
            [new(1, DateTimeOffset.UtcNow, 2048)]);

        var json = CliPipeProtocol.SerializeResponse(response);
        var roundTripped = CliPipeProtocol.DeserializeResponse(json);

        Assert.Equal(response.Success, roundTripped.Success);
        Assert.Equal(response.Message, roundTripped.Message);
        Assert.Equal(response.ExitCode, roundTripped.ExitCode);
        Assert.Equal(response.Disks, roundTripped.Disks);
        Assert.Equal(response.Snapshots, roundTripped.Snapshots);
    }

    [Fact]
    public void DeserializeResponse_NullJsonLiteral_FallsBackToFailureResponse()
    {
        var result = CliPipeProtocol.DeserializeResponse("null");

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Message);
        Assert.Null(result.Disks);
        Assert.Equal(1, result.ExitCode);
        Assert.Null(result.Snapshots);
    }

    [Fact]
    public void DeserializeResponse_MalformedJson_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => CliPipeProtocol.DeserializeResponse("{ not valid json"));
    }

    [Fact]
    public void SerializeResponse_OmittedOptionalSnapshots_DefaultsToNull()
    {
        var response = new CliResponse(true, "Unmounted R:.", null, 0);

        var json = CliPipeProtocol.SerializeResponse(response);
        var roundTripped = CliPipeProtocol.DeserializeResponse(json);

        Assert.Null(roundTripped.Snapshots);
    }
}
