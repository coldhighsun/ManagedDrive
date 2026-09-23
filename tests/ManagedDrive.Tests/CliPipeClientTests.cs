using System.Diagnostics;
using System.IO.Pipes;
using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public sealed class CliPipeClientTests : IDisposable
{
    public CliPipeClientTests()
    {
        CliPipeClient.TestPipeNameOverride = $"ManagedDrive-Test-Cli-{Guid.NewGuid()}";
        CliPipeClient.TestReadTimeoutOverride = TimeSpan.FromMilliseconds(200);
    }

    public void Dispose()
    {
        CliPipeClient.TestPipeNameOverride = null;
        CliPipeClient.TestReadTimeoutOverride = null;
    }

    [Fact]
    public async Task TrySend_ServerRespondsPromptly_ReturnsTrueWithDeserializedResponse()
    {
        var pipeName = CliPipeClient.TestPipeNameOverride!;
        var expected = new CliResponse(true, "Mounted R:.", null, 0);

        await using var server = new EchoingCliServer(pipeName, expected);
        var serverTask = server.RunOnceAsync();

        var connected = CliPipeClient.TrySend(["mount", "R:"], out var response);

        await serverTask;
        Assert.True(connected);
        Assert.Equal(expected.Success, response.Success);
        Assert.Equal(expected.Message, response.Message);
        Assert.Equal(expected.ExitCode, response.ExitCode);
    }

    [Fact(Timeout = 10_000)]
    public async Task TrySend_ServerAcceptsButNeverResponds_ReturnsFalseWithoutHangingPastTheOverriddenTimeout()
    {
        var pipeName = CliPipeClient.TestPipeNameOverride!;

        using var serverCts = new CancellationTokenSource();

        // Constructed synchronously (not inside Task.Run) so the pipe is already listening before
        // TrySend attempts to connect — otherwise a busy thread pool could delay pipe creation past
        // TrySend's fixed Connect timeout, and the test would pass via the "no listener" path
        // instead of exercising the read-timeout path it's meant to verify.
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(serverCts.Token);

            // Accept the connection and read the request, but deliberately never write a
            // response line back — this is the "wedged UI thread" scenario the read timeout
            // guards against.
            using var reader = new StreamReader(server, leaveOpen: true);
            await reader.ReadLineAsync(serverCts.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, serverCts.Token);
        }, TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();

        var connected = CliPipeClient.TrySend(["list"], out var response);

        stopwatch.Stop();

        Assert.False(connected);
        Assert.False(response.Success);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"TrySend blocked for {stopwatch.Elapsed}; the overridden 200ms read timeout should have cancelled the read.");

        serverCts.Cancel();
        await AwaitIgnoringCancellationAsync(serverTask);
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

    private sealed class EchoingCliServer(string pipeName, CliResponse response) : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _server = new(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        public async Task RunOnceAsync()
        {
            await _server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

            using var reader = new StreamReader(_server, leaveOpen: true);
            await using var writer = new StreamWriter(_server, leaveOpen: true) { AutoFlush = true };

            await reader.ReadLineAsync(TestContext.Current.CancellationToken);
            await writer.WriteLineAsync(CliPipeProtocol.SerializeResponse(response));
        }

        public async ValueTask DisposeAsync() => await _server.DisposeAsync();
    }
}
