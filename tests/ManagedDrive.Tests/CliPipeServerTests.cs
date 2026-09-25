using System.IO.Pipes;
using System.Security.Principal;
using ManagedDrive.App.Cli;
using ManagedDrive.Cli.Core;
using ManagedDrive.HelperProtocol;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the CLI pipe server's pipe security and how it serves concurrent clients.
/// </summary>
public sealed class CliPipeServerTests
{
    /// <summary>
    /// Upper bound on a client connecting when a pipe instance should be available.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Pipe name unique to this test.
    /// </summary>
    private readonly string _pipeName = $"ManagedDrive-Test-CliServer-{Guid.NewGuid()}";

    /// <summary>
    /// The first instance refuses to join a pipe another process already created under the name.
    /// </summary>
    [Fact]
    public void CreatePipe_FirstInstanceWhileNameHeldByAnotherPipe_Throws()
    {
        // A squatter's pipe that would accept further instances like ours — joining it must fail.
        using var squatter = new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, CliPipeServer.MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Assert.Throws<UnauthorizedAccessException>(() => CliPipeServer.CreatePipe(_pipeName, firstInstance: true));
    }

    /// <summary>
    /// Further instances join the server's own pipe.
    /// </summary>
    [Fact]
    public void CreatePipe_FurtherInstanceWhileOwnInstanceAlive_Succeeds()
    {
        using var first = CliPipeServer.CreatePipe(_pipeName, firstInstance: true);

        using var second = CliPipeServer.CreatePipe(_pipeName, firstInstance: false);

        Assert.NotNull(second);
    }

    /// <summary>
    /// The pipe admits its creator and is owned by the creator's token owner, which the client checks.
    /// </summary>
    [Fact]
    public async Task CreatePipe_FreeName_AcceptsThisUserAndIsOwnedByItsTokenOwner()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var server = CliPipeServer.CreatePipe(_pipeName, firstInstance: true);
        var connectTask = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await client.ConnectAsync(TestContext.Current.CancellationToken);
        await connectTask;

        Assert.Equal(identity.Owner, PipeSecurityRules.TryGetOwner(client));
    }

    /// <summary>
    /// A second client connects while the first command still runs, and its command runs after it.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task Start_SecondClientWhileFirstCommandRuns_ConnectsAtOnceAndRunsAfterIt()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new List<string>();
        using var server = new CliPipeServer(_pipeName, async request =>
        {
            lock (executed)
            {
                executed.Add(request.Args[0]);
            }

            if (request.Args[0] == "first")
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }

            return new(true, $"ran {request.Args[0]}", null, 0);
        });
        server.Start();

        await using var firstClient = await ConnectAsync();
        var firstResponse = SendAsync(firstClient, "first");
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Connecting succeeds while the first command still holds the server.
        await using var secondClient = await ConnectAsync();
        var secondResponse = SendAsync(secondClient, "second");
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        lock (executed)
        {
            Assert.Equal(["first"], executed);
        }

        releaseFirst.SetResult();

        Assert.Equal("ran first", (await firstResponse).Message);
        Assert.Equal("ran second", (await secondResponse).Message);
        Assert.Equal(["first", "second"], executed);
    }

    /// <summary>
    /// A command still waiting for the one before it after the queue timeout is refused without running.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task Start_CommandQueuedPastTheQueueTimeout_IsRefusedWithoutRunning()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new List<string>();
        using var server = new CliPipeServer(
            _pipeName,
            async request =>
            {
                lock (executed)
                {
                    executed.Add(request.Args[0]);
                }

                if (request.Args[0] == "first")
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }

                return new(true, $"ran {request.Args[0]}", null, 0);
            },
            executionQueueTimeout: TimeSpan.FromMilliseconds(200));
        server.Start();

        await using var firstClient = await ConnectAsync();
        var firstResponse = SendAsync(firstClient, "first");
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await using var secondClient = await ConnectAsync();
        var secondResponse = await SendAsync(secondClient, "second");

        releaseFirst.SetResult();
        await firstResponse;
        Assert.False(secondResponse.Success);
        Assert.Equal(1, secondResponse.ExitCode);
        Assert.Equal(CliPipeServer.BusyMessage, secondResponse.Message);
        Assert.Equal(["first"], executed);
    }

    /// <summary>
    /// Connects a raw client to the server under test. Deliberately not <see cref="CliPipeClient"/>:
    /// its pipe-name override is static and shared with tests running in parallel.
    /// </summary>
    private async Task<NamedPipeClientStream> ConnectAsync()
    {
        var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(ConnectTimeout, TestContext.Current.CancellationToken);
        return client;
    }

    /// <summary>
    /// Sends a one-argument request over <paramref name="client"/> and reads the response.
    /// </summary>
    private static async Task<CliResponse> SendAsync(NamedPipeClientStream client, string arg)
    {
        using var reader = new StreamReader(client, leaveOpen: true);
        await using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(CliPipeProtocol.SerializeRequest([arg], null));
        var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        return CliPipeProtocol.DeserializeResponse(line!);
    }
}
