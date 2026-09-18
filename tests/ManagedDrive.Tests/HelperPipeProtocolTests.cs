using ManagedDrive.HelperProtocol;

namespace ManagedDrive.Tests;

public sealed class HelperPipeProtocolTests
{
    [Fact]
    public void SerializeThenDeserializeRequest_RoundTripsAllFields()
    {
        var request = new HelperRequest(HelperPipeProtocol.OpPublish, "X:", @"\Device\Volume{guid}");

        var json = HelperPipeProtocol.SerializeRequest(request);
        var roundTripped = HelperPipeProtocol.DeserializeRequest(json);

        Assert.Equal(request, roundTripped);
    }

    [Fact]
    public void SerializeThenDeserializeRequest_NullLetterAndDevicePath_RoundTrips()
    {
        var request = new HelperRequest(HelperPipeProtocol.OpPing, null, null);

        var json = HelperPipeProtocol.SerializeRequest(request);
        var roundTripped = HelperPipeProtocol.DeserializeRequest(json);

        Assert.Equal(request, roundTripped);
    }

    [Fact]
    public void SerializeThenDeserializeResponse_RoundTripsAllFields()
    {
        var response = new HelperResponse(true, "Published successfully.");

        var json = HelperPipeProtocol.SerializeResponse(response);
        var roundTripped = HelperPipeProtocol.DeserializeResponse(json);

        Assert.Equal(response, roundTripped);
    }

    [Fact]
    public void SerializeThenDeserializeResponse_FailureWithMessage_RoundTrips()
    {
        var response = new HelperResponse(false, "Access denied.");

        var json = HelperPipeProtocol.SerializeResponse(response);
        var roundTripped = HelperPipeProtocol.DeserializeResponse(json);

        Assert.Equal(response, roundTripped);
    }

    [Fact]
    public void DeserializeRequest_JsonNull_ReturnsEmptyOpFallback()
    {
        var result = HelperPipeProtocol.DeserializeRequest("null");

        Assert.Equal(new HelperRequest(string.Empty, null, null), result);
    }

    [Fact]
    public void DeserializeResponse_JsonNull_ReturnsFailureFallback()
    {
        var result = HelperPipeProtocol.DeserializeResponse("null");

        Assert.Equal(new HelperResponse(false, string.Empty), result);
    }

    [Fact]
    public void SerializeRequest_ProducesSingleLineJson()
    {
        var request = new HelperRequest(HelperPipeProtocol.OpUnpublish, "Z:", null);

        var json = HelperPipeProtocol.SerializeRequest(request);

        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\r', json);
    }
}
