using System.Collections.Concurrent;
using System.IO.Pipes;
using ManagedDrive.HelperProtocol;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for how <see cref="PipeListener"/> accepts and serves concurrent pipe clients.
/// </summary>
public sealed class PipeListenerTests : IDisposable
{
    /// <summary>
    /// Upper bound on a client connecting when a pipe instance should be available.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Pipe name unique to this test.
    /// </summary>
    private readonly string _pipeName = $"ManagedDrive-Test-Listener-{Guid.NewGuid()}";

    /// <summary>
    /// Stops the listener under test.
    /// </summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Clients opened by the test, disposed with it.
    /// </summary>
    private readonly List<NamedPipeClientStream> _clients = [];

    /// <summary>
    /// Stops the listener and disposes the clients.
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _cts.Dispose();
    }

    /// <summary>
    /// A client being served doesn't keep the next one from connecting and being served too.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task RunAsync_SecondClientWhileFirstIsServed_IsServedConcurrently()
    {
        var served = new ConcurrentQueue<string>();
        var bothServed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = CreateListener(maxInstances: 4, async (pipe, ct) =>
        {
            served.Enqueue(await ReadLineAsync(pipe, ct));
            if (served.Count == 2)
            {
                bothServed.TrySetResult();
            }

            // Hold every connection open until both are being served at once.
            await bothServed.Task.WaitAsync(ct);
        });
        var run = listener.RunAsync(_cts.Token);

        await SendAsync(await ConnectAsync(), "first");
        await SendAsync(await ConnectAsync(), "second");

        await bothServed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["first", "second"], served.Order());
        _cts.Cancel();
        await run;
    }

    /// <summary>
    /// Once every instance is taken, a further client can't connect until one frees up.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task RunAsync_AllInstancesBusy_NextClientConnectsOnceOneFrees()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = CreateListener(maxInstances: 2, async (pipe, ct) => await release.Task.WaitAsync(ct));
        var run = listener.RunAsync(_cts.Token);
        await ConnectAsync();
        await ConnectAsync();

        var blocked = NewClient();
        await Assert.ThrowsAsync<TimeoutException>(
            () => blocked.ConnectAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
        release.SetResult();
        await blocked.ConnectAsync(ConnectTimeout, TestContext.Current.CancellationToken);

        Assert.True(blocked.IsConnected);
        _cts.Cancel();
        await run;
    }

    /// <summary>
    /// Only an instance created while no other exists claims the name as its first instance;
    /// instances created alongside a connected one join it.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task RunAsync_InstanceCreatedWhileAClientIsConnected_IsNotCreatedAsFirstInstance()
    {
        var firstInstanceArgs = new ConcurrentQueue<bool>();
        var secondCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new PipeListener(
            maxInstances: 4,
            firstInstance =>
            {
                firstInstanceArgs.Enqueue(firstInstance);
                if (firstInstanceArgs.Count == 2)
                {
                    secondCreated.TrySetResult();
                }

                return CreatePipe(4, firstInstance);
            },
            async (pipe, ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct),
            ex => { },
            ex => { },
            TimeSpan.FromMilliseconds(50));
        var run = listener.RunAsync(_cts.Token);

        await ConnectAsync();
        await secondCreated.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal([true, false], firstInstanceArgs.Take(2));
        _cts.Cancel();
        await run;
    }

    /// <summary>
    /// A failure to create the pipe is reported and retried rather than ending the listener.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task RunAsync_CreateFailsOnce_ReportsItAndRetries()
    {
        var attempts = 0;
        var createFailures = new ConcurrentQueue<Exception>();
        var servedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new PipeListener(
            maxInstances: 4,
            firstInstance => Interlocked.Increment(ref attempts) == 1
                ? throw new IOException("name taken")
                : CreatePipe(4, firstInstance),
            async (pipe, ct) => servedTcs.TrySetResult(await ReadLineAsync(pipe, ct)),
            createFailures.Enqueue,
            ex => { },
            TimeSpan.FromMilliseconds(50));
        var run = listener.RunAsync(_cts.Token);

        await SendAsync(await ConnectAsync(), "hello");

        Assert.Equal("hello", await servedTcs.Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("name taken", Assert.Single(createFailures).Message);
        _cts.Cancel();
        await run;
    }

    /// <summary>
    /// A client whose serving throws is reported, and later clients are still served.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task RunAsync_ServingThrows_ReportsItAndKeepsServing()
    {
        var connectionFailures = new ConcurrentQueue<Exception>();
        var servedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new PipeListener(
            maxInstances: 4,
            firstInstance => CreatePipe(4, firstInstance),
            async (pipe, ct) =>
            {
                var line = await ReadLineAsync(pipe, ct);
                if (line == "bad")
                {
                    throw new InvalidOperationException("bad request");
                }

                servedTcs.TrySetResult(line);
            },
            ex => { },
            connectionFailures.Enqueue,
            TimeSpan.FromMilliseconds(50));
        var run = listener.RunAsync(_cts.Token);

        await SendAsync(await ConnectAsync(), "bad");
        await SendAsync(await ConnectAsync(), "good");

        Assert.Equal("good", await servedTcs.Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Contains(connectionFailures, ex => ex.Message == "bad request");
        _cts.Cancel();
        await run;
    }

    /// <summary>
    /// Stopping the listener cancels the clients being served and waits for them.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task RunAsync_Cancelled_CancelsServingAndCompletesOnceConnectionsEnd()
    {
        var serving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var servingEnded = false;
        var listener = CreateListener(maxInstances: 4, async (pipe, ct) =>
        {
            serving.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                servingEnded = true;
            }
        });
        var run = listener.RunAsync(_cts.Token);
        await ConnectAsync();
        await serving.Task.WaitAsync(TestContext.Current.CancellationToken);

        _cts.Cancel();
        await run;

        Assert.True(servingEnded);
    }

    /// <summary>
    /// Creates a listener for this test's pipe that ignores failures.
    /// </summary>
    /// <param name="maxInstances">Most pipe instances alive at once.</param>
    /// <param name="serveAsync">Serves one connected client.</param>
    /// <returns>The listener, not yet running.</returns>
    private PipeListener CreateListener(int maxInstances, Func<NamedPipeServerStream, CancellationToken, Task> serveAsync) =>
        new(
            maxInstances,
            firstInstance => CreatePipe(maxInstances, firstInstance),
            serveAsync,
            ex => { },
            ex => { },
            TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Creates a listening instance of this test's pipe.
    /// </summary>
    /// <param name="maxInstances">The pipe's instance limit.</param>
    /// <param name="firstInstance">Whether to create it as the name's first instance.</param>
    /// <returns>The pipe instance.</returns>
    private NamedPipeServerStream CreatePipe(int maxInstances, bool firstInstance) =>
        new(
            _pipeName,
            PipeDirection.InOut,
            maxInstances,
            PipeTransmissionMode.Byte,
            firstInstance ? PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance : PipeOptions.Asynchronous);

    /// <summary>
    /// Creates a client for this test's pipe, disposed with the test.
    /// </summary>
    /// <returns>The client, not yet connected.</returns>
    private NamedPipeClientStream NewClient()
    {
        var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _clients.Add(client);
        return client;
    }

    /// <summary>
    /// Connects a new client to this test's pipe.
    /// </summary>
    /// <returns>The connected client, disposed with the test.</returns>
    private async Task<NamedPipeClientStream> ConnectAsync()
    {
        var client = NewClient();
        await client.ConnectAsync(ConnectTimeout, TestContext.Current.CancellationToken);
        return client;
    }

    /// <summary>
    /// Writes <paramref name="line"/> to <paramref name="client"/>.
    /// </summary>
    /// <param name="client">A connected client.</param>
    /// <param name="line">The line to write.</param>
    private static async Task SendAsync(NamedPipeClientStream client, string line)
    {
        await using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(line);
    }

    /// <summary>
    /// Reads one line from <paramref name="pipe"/>.
    /// </summary>
    /// <param name="pipe">A connected pipe.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The line, or an empty string if the pipe closed first.</returns>
    private static async Task<string> ReadLineAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        return await reader.ReadLineAsync(ct) ?? string.Empty;
    }
}
