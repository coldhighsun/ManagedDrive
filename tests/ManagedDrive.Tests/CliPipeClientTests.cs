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
        CliPipeClient.TestCurrentUserSidOverride = null;
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
    public async Task TrySend_ServerAcceptsButNeverResponds_ReturnsTrueWithNoResponseFailureWithoutHanging()
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

        var answered = CliPipeClient.TrySend(["list"], out var response);

        stopwatch.Stop();

        // The request was delivered, so the caller must report the failure rather than resend it.
        Assert.True(answered);
        Assert.False(response.Success);
        Assert.Equal(1, response.ExitCode);
        Assert.Equal(CliPipeClient.NoResponseMessage, response.Message);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"TrySend blocked for {stopwatch.Elapsed}; the overridden 200ms read timeout should have cancelled the read.");

        serverCts.Cancel();
        await AwaitIgnoringCancellationAsync(serverTask);
    }

    [Fact(Timeout = 10_000)]
    public async Task TrySend_ServerNeverReadsRequest_ReturnsTrueWithNoResponseFailureWithoutHanging()
    {
        var pipeName = CliPipeClient.TestPipeNameOverride!;

        // A zero-size inbound buffer, as the real server uses, makes the client's write complete
        // only once the server reads. The client can connect to this instance without the server
        // ever waiting for a connection, so nothing ever reads the request.
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0);

        var stopwatch = Stopwatch.StartNew();

        // On a dedicated thread so a regression that blocks the write forever still lets the test
        // time out instead of hanging the run.
        var (answered, response) = await Task.Factory.StartNew(
            () => (CliPipeClient.TrySend(["list"], out var r), r),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).WaitAsync(TestContext.Current.CancellationToken);

        stopwatch.Stop();

        // The request may still be read and run later, so it must not be reported as undelivered.
        Assert.True(answered);
        Assert.False(response.Success);
        Assert.Equal(1, response.ExitCode);
        Assert.Equal(CliPipeClient.NoResponseMessage, response.Message);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"TrySend blocked for {stopwatch.Elapsed}; the overridden 200ms timeout should have abandoned the write.");
    }

    [Fact(Timeout = 10_000)]
    public async Task TrySend_ServerClosesAfterReadingRequest_ReturnsTrueWithConnectionClosedFailure()
    {
        var pipeName = CliPipeClient.TestPipeNameOverride!;

        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

            using var reader = new StreamReader(server, leaveOpen: true);
            await reader.ReadLineAsync(TestContext.Current.CancellationToken);
            server.Disconnect();
        }, TestContext.Current.CancellationToken);

        var answered = CliPipeClient.TrySend(["unmount", "R:"], out var response);

        await serverTask;
        Assert.True(answered);
        Assert.False(response.Success);
        Assert.Equal(1, response.ExitCode);
        Assert.Equal(CliPipeClient.ConnectionClosedMessage, response.Message);
    }

    [Fact(Timeout = 10_000)]
    public async Task TrySend_ServerRespondsWithMalformedJson_ReturnsTrueWithInvalidResponseFailure()
    {
        var pipeName = CliPipeClient.TestPipeNameOverride!;

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

        var answered = CliPipeClient.TrySend(["list"], out var response);

        await serverTask;
        Assert.True(answered);
        Assert.False(response.Success);
        Assert.Equal(CliPipeClient.InvalidResponseMessage, response.Message);
    }

    [Fact(Timeout = 10_000)]
    public async Task TrySend_ServerDropsConnectionRightAfterAccepting_DoesNotThrow()
    {
        var pipeName = CliPipeClient.TestPipeNameOverride!;

        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            server.Disconnect();
        }, TestContext.Current.CancellationToken);

        var answered = false;
        CliResponse? response = null;
        var exception = Record.Exception(() =>
        {
            answered = CliPipeClient.TrySend(["list"], out var r);
            response = r;
        });

        await serverTask;

        // Depending on whether the drop lands before or after the request is written, this is
        // either "not delivered" (false) or a delivered-but-unanswered failure — never a throw.
        Assert.Null(exception);
        Assert.False(answered && response!.Success);
    }

    [Fact]
    public void TrySend_ServerPipeDeniesThisUser_ReturnsTrueWithAccessDeniedFailure()
    {
        var pipeName = CliPipeClient.TestPipeNameOverride!;

        // Only SYSTEM may connect — this test process's own (non-SYSTEM) user is denied, the same
        // way an elevated or other-user instance's pipe would reject it.
        var security = new PipeSecurity();
        security.AddAccessRule(new(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        using var server = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

        var answered = CliPipeClient.TrySend(["list"], out var response);

        Assert.True(answered);
        Assert.False(response.Success);
        Assert.Equal(1, response.ExitCode);
        Assert.Equal(CliPipeClient.AccessDeniedMessage, response.Message);
    }

    [Fact(Timeout = 10_000)]
    public async Task TrySend_ServerPipeOwnedByAnotherUser_ReturnsTrueWithUntrustedServerFailureWithoutSendingRequest()
    {
        var pipeName = CliPipeClient.TestPipeNameOverride!;

        // The pipe is explicitly owned by this process's user, while the client is told it runs as
        // someone else — so from the client's point of view another, unprivileged user created it.
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new(identity.User!, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        security.SetOwner(identity.User!);
        CliPipeClient.TestCurrentUserSidOverride = new("S-1-5-21-1000000000-2000000000-3000000000-1001");

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
                // The client connected and hung up before this accept ran — nothing was sent.
                return null;
            }

            using var reader = new StreamReader(server, leaveOpen: true);
            return await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var answered = CliPipeClient.TrySend(["mount", "R:", "--password", "secret"], out var response);

        Assert.True(answered);
        Assert.False(response.Success);
        Assert.Equal(1, response.ExitCode);
        Assert.Equal(CliPipeClient.UntrustedServerMessage, response.Message);
        Assert.Null(await serverTask);
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
