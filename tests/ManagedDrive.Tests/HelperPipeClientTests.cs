using System.Diagnostics;
using System.IO.Pipes;
using ManagedDrive.HelperProtocol;

namespace ManagedDrive.Tests;

public sealed class HelperPipeClientTests : IDisposable
{
    public HelperPipeClientTests()
    {
        HelperPipeClient.TestPipeNameOverride = $"ManagedDrive-Test-Helper-{Guid.NewGuid()}";
        HelperPipeClient.TestReadTimeoutOverride = TimeSpan.FromMilliseconds(200);
    }

    public void Dispose()
    {
        HelperPipeClient.TestPipeNameOverride = null;
        HelperPipeClient.TestReadTimeoutOverride = null;
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
