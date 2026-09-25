using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ManagedDrive.HelperProtocol;

namespace ManagedDrive.Tests;

public sealed class HelperPipeClientTests : IDisposable
{
    public HelperPipeClientTests()
    {
        HelperPipeClient.TestPipeNameOverride = $"ManagedDrive-Test-Helper-{Guid.NewGuid()}";
        HelperPipeClient.TestReadTimeoutOverride = TimeSpan.FromMilliseconds(200);

        // The fake services below are owned by this process's token owner, not LocalSystem.
        using var identity = WindowsIdentity.GetCurrent();
        HelperPipeClient.TestTrustedOwnerOverride = identity.Owner;
    }

    public void Dispose()
    {
        HelperPipeClient.TestPipeNameOverride = null;
        HelperPipeClient.TestReadTimeoutOverride = null;
        HelperPipeClient.TestTrustedOwnerOverride = null;
    }

    [Fact(Timeout = 10_000)]
    public async Task IsServiceAvailable_PipeNotOwnedByTheService_ReturnsFalseWithUntrustedOwnerReasonWithoutSendingRequest()
    {
        var pipeName = HelperPipeClient.TestPipeNameOverride!;
        using var identity = WindowsIdentity.GetCurrent();
        HelperPipeClient.TestTrustedOwnerOverride = null;
        var security = new PipeSecurity();
        security.AddAccessRule(new(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetOwner(identity.User!);
        using var server = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            }
            catch (IOException)
            {
                // The client may already have hung up before the wait began.
                return null;
            }

            using var reader = new StreamReader(server, leaveOpen: true);
            return await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var connected = HelperPipeClient.IsServiceAvailable(out var failureReason);

        Assert.False(connected);
        Assert.Contains("not by LocalSystem", failureReason);
        Assert.Null(await serverTask);
    }

    [Fact]
    public async Task TryPublish_ServiceRespondsPromptly_ReturnsTrueWithDeserializedResponse()
    {
        var pipeName = HelperPipeClient.TestPipeNameOverride!;
        var expected = new HelperResponse(true, "Published successfully.");

        var serverTask = RunEchoingServerOnceAsync(pipeName, expected);

        var connected = HelperPipeClient.TryPublish("X:", @"\Device\Volume{guid}", out var response);

        await serverTask;
        Assert.True(connected);
        Assert.Equal(expected, response);
    }

    [Fact(Timeout = 10_000)]
    public async Task TryPublish_ServiceAcceptsButNeverResponds_ReturnsFalseWithoutHangingPastTheOverriddenTimeout()
    {
        var pipeName = HelperPipeClient.TestPipeNameOverride!;

        using var serverCts = new CancellationTokenSource();

        // Constructed synchronously (not inside Task.Run) so the pipe is already listening before
        // TryPublish attempts to connect — otherwise a busy thread pool could delay pipe creation
        // past TryPublish's fixed Connect timeout, and the test would pass via the "no listener"
        // path instead of exercising the read-timeout path it's meant to verify.
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(serverCts.Token);

            // Accept the connection and read the request, but deliberately never write a
            // response line back — this is the "wedged service" scenario the read timeout
            // guards against.
            using var reader = new StreamReader(server, leaveOpen: true);
            await reader.ReadLineAsync(serverCts.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, serverCts.Token);
        }, TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();

        var connected = HelperPipeClient.TryPublish("X:", @"\Device\Volume{guid}", out var response);

        stopwatch.Stop();

        Assert.False(connected);
        Assert.False(response.Success);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"TryPublish blocked for {stopwatch.Elapsed}; the overridden 200ms read timeout should have cancelled the read.");

        serverCts.Cancel();
        await AwaitIgnoringCancellationAsync(serverTask);
    }

    [Fact]
    public void IsServiceAvailable_NoListener_ReturnsFalseWithConnectFailureReason()
    {
        var available = HelperPipeClient.IsServiceAvailable(out var failureReason);

        Assert.False(available);
        Assert.NotNull(failureReason);
        Assert.Contains("connect failed", failureReason);
    }

    [Fact]
    public async Task IsServiceAvailable_ServiceRespondsWithPong_ReturnsTrueWithNullFailureReason()
    {
        var pipeName = HelperPipeClient.TestPipeNameOverride!;
        var serverTask = RunEchoingServerOnceAsync(pipeName, new HelperResponse(true, "pong"));

        var available = HelperPipeClient.IsServiceAvailable(out var failureReason);

        await serverTask;
        Assert.True(available);
        Assert.Null(failureReason);
    }

    [Fact(Timeout = 10_000)]
    public async Task IsServiceAvailable_ServiceRespondsWithMalformedJson_ReturnsFalseWithRequestFailureReason()
    {
        var pipeName = HelperPipeClient.TestPipeNameOverride!;

        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

            using var reader = new StreamReader(server, leaveOpen: true);
            await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
            await reader.ReadLineAsync(TestContext.Current.CancellationToken);
            await writer.WriteLineAsync("{not json");
        }, TestContext.Current.CancellationToken);

        var available = HelperPipeClient.IsServiceAvailable(out var failureReason);

        await serverTask;
        Assert.False(available);
        Assert.NotNull(failureReason);
        Assert.Contains("request failed", failureReason);
    }

    [Fact(Timeout = 10_000)]
    public async Task IsServiceAvailable_ServiceDropsConnectionRightAfterAccepting_ReturnsFalseWithoutThrowing()
    {
        var pipeName = HelperPipeClient.TestPipeNameOverride!;

        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            server.Disconnect();
        }, TestContext.Current.CancellationToken);

        var available = true;
        string? failureReason = null;
        var exception = Record.Exception(() => available = HelperPipeClient.IsServiceAvailable(out failureReason));

        await serverTask;
        Assert.Null(exception);
        Assert.False(available);
        Assert.NotNull(failureReason);
    }

    private static async Task RunEchoingServerOnceAsync(string pipeName, HelperResponse response)
    {
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        using var reader = new StreamReader(server, leaveOpen: true);
        await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

        await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        await writer.WriteLineAsync(HelperPipeProtocol.SerializeResponse(response));
    }

    private static async Task AwaitIgnoringCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The client side closing its handle while the server awaits more data surfaces as a
            // broken-pipe IOException here; expected once the test tears the server down.
        }
    }
}
