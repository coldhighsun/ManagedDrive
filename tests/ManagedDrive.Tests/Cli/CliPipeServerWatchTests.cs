using System.IO.Pipes;
using ManagedDrive.App.Cli;
using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for how the CLI pipe server streams <c>watch</c> events.
/// </summary>
public sealed class CliPipeServerWatchTests
{
    /// <summary>
    /// Upper bound on anything that should happen promptly.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Pipe name unique to this test.
    /// </summary>
    private readonly string _pipeName = $"ManagedDrive-Test-CliWatch-{Guid.NewGuid()}";

    [Fact]
    public async Task Watch_EventRaised_IsStreamedAsATextLine()
    {
        var source = new FakeCliEventSource();
        using var server = StartServer(source);
        await using var client = await WatchClient.OpenAsync(_pipeName, ["watch"]);
        await source.WaitForSubscribersAsync(1);

        source.Raise(new(CliEventNames.Mounted, "R:", DateTimeOffset.Now, "RAM Disk"));

        var response = await client.ReadAsync();
        Assert.True(response.Success);
        Assert.False(response.Json);
        Assert.EndsWith("mounted R: RAM Disk", response.Message);
    }

    [Fact]
    public async Task Watch_WithJson_StreamsOneJsonDocumentPerEvent()
    {
        var source = new FakeCliEventSource();
        using var server = StartServer(source);
        await using var client = await WatchClient.OpenAsync(_pipeName, ["watch", "--json"]);
        await source.WaitForSubscribersAsync(1);

        source.Raise(new(CliEventNames.SaveFailed, "S:", DateTimeOffset.Now, "disk full"));

        var response = await client.ReadAsync();
        Assert.True(response.Json);
        using var document = System.Text.Json.JsonDocument.Parse(response.Message);
        Assert.Equal("save-failed", document.RootElement.GetProperty("Event").GetString());
        Assert.Equal("S:", document.RootElement.GetProperty("MountPoint").GetString());
        Assert.Equal("disk full", document.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task Watch_BurstOfEvents_ArrivesInOrderWithoutLoss()
    {
        var source = new FakeCliEventSource();
        using var server = StartServer(source);
        await using var client = await WatchClient.OpenAsync(_pipeName, ["watch"]);
        await source.WaitForSubscribersAsync(1);

        for (var i = 0; i < 50; i++)
        {
            source.Raise(new(CliEventNames.Mounted, $"D{i}:", DateTimeOffset.Now));
        }

        for (var i = 0; i < 50; i++)
        {
            Assert.EndsWith($"mounted D{i}:", (await client.ReadAsync()).Message);
        }
    }

    [Fact]
    public async Task Watch_WhileWatching_OrdinaryCommandsStillRun()
    {
        var source = new FakeCliEventSource();
        using var server = StartServer(source);
        await using var watcher = await WatchClient.OpenAsync(_pipeName, ["watch"]);
        await source.WaitForSubscribersAsync(1);

        await using var other = await WatchClient.OpenAsync(_pipeName, ["list"]);
        var response = await other.ReadAsync();

        Assert.True(response.Success);
        Assert.Equal("ran list", response.Message);
    }

    [Fact]
    public async Task Watch_ClientDisconnects_StopsListeningAndFreesTheSlot()
    {
        var source = new FakeCliEventSource();
        using var server = StartServer(source);
        var client = await WatchClient.OpenAsync(_pipeName, ["watch"]);
        await source.WaitForSubscribersAsync(1);

        await client.DisposeAsync();

        await source.WaitForSubscribersAsync(0);
    }

    [Fact]
    public async Task Watch_BeyondTheLimit_IsRefusedUntilOneLeaves()
    {
        var source = new FakeCliEventSource();
        using var server = StartServer(source);
        var watchers = new List<WatchClient>();
        for (var i = 0; i < CliPipeServer.MaxWatchers; i++)
        {
            watchers.Add(await WatchClient.OpenAsync(_pipeName, ["watch"]));
        }

        await source.WaitForSubscribersAsync(CliPipeServer.MaxWatchers);

        await using (var refused = await WatchClient.OpenAsync(_pipeName, ["watch"]))
        {
            var response = await refused.ReadAsync();
            Assert.False(response.Success);
            Assert.Equal(1, response.ExitCode);
            Assert.Equal(CliPipeServer.TooManyWatchersMessage, response.Message);
        }

        await watchers[0].DisposeAsync();
        await source.WaitForSubscribersAsync(CliPipeServer.MaxWatchers - 1);

        await using var admitted = await WatchClient.OpenAsync(_pipeName, ["watch"]);
        await source.WaitForSubscribersAsync(CliPipeServer.MaxWatchers);
        source.Raise(new(CliEventNames.Unmounted, "R:", DateTimeOffset.Now));
        Assert.EndsWith("unmounted R:", (await admitted.ReadAsync()).Message);

        foreach (var watcher in watchers.Skip(1))
        {
            await watcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task Watch_ServerWithoutAnEventSource_RefusesTheClient()
    {
        using var server = StartServer(eventSource: null);
        await using var client = await WatchClient.OpenAsync(_pipeName, ["watch"]);

        var response = await client.ReadAsync();

        Assert.False(response.Success);
        Assert.Equal(1, response.ExitCode);
    }

    [Fact]
    public async Task Watch_ServerDisposed_EndsTheStream()
    {
        var source = new FakeCliEventSource();
        var server = StartServer(source);
        await using var client = await WatchClient.OpenAsync(_pipeName, ["watch"]);
        await source.WaitForSubscribersAsync(1);

        server.Dispose();

        Assert.Null(await client.ReadLineOrNullAsync());
    }

    /// <summary>
    /// Starts a server whose ordinary commands answer <c>ran &lt;first argument&gt;</c>.
    /// </summary>
    /// <param name="eventSource">What feeds <c>watch</c> clients.</param>
    private CliPipeServer StartServer(ICliEventSource? eventSource)
    {
        var server = new CliPipeServer(
            _pipeName,
            request => Task.FromResult(new CliOutcome(true, $"ran {request.Args[0]}", null, 0)),
            eventSource: eventSource);
        server.Start();
        return server;
    }

    /// <summary>
    /// A raw client that has sent one request and then only reads. Deliberately not
    /// <see cref="CliPipeClient"/>: its pipe-name override is static and shared with tests running in parallel.
    /// </summary>
    private sealed class WatchClient : IAsyncDisposable
    {
        /// <summary>
        /// The connected pipe.
        /// </summary>
        private readonly NamedPipeClientStream _pipe;

        /// <summary>
        /// Reads the response lines.
        /// </summary>
        private readonly StreamReader _reader;

        private WatchClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new(pipe, leaveOpen: true);
        }

        /// <summary>
        /// Connects to <paramref name="pipeName"/> and sends <paramref name="args"/> as the request.
        /// </summary>
        /// <param name="pipeName">The server's pipe.</param>
        /// <param name="args">The request's arguments.</param>
        /// <returns>The client, ready to read.</returns>
        public static async Task<WatchClient> OpenAsync(string pipeName, string[] args)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(Patience, TestContext.Current.CancellationToken);
            await using var writer = new StreamWriter(pipe, leaveOpen: true);
            writer.AutoFlush = true;
            await writer.WriteLineAsync(CliPipeProtocol.SerializeRequest(args, null));
            return new(pipe);
        }

        /// <summary>
        /// Reads the next response line, failing the test if none comes in time or the stream ended.
        /// </summary>
        /// <returns>The deserialized response.</returns>
        public async Task<CliResponse> ReadAsync()
        {
            var line = await ReadLineOrNullAsync();
            Assert.NotNull(line);
            return CliPipeProtocol.DeserializeResponse(line);
        }

        /// <summary>
        /// Reads the next raw line.
        /// </summary>
        /// <returns>The line, or <c>null</c> when the server closed the stream.</returns>
        public async Task<string?> ReadLineOrNullAsync() =>
            await _reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Patience);

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            return _pipe.DisposeAsync();
        }
    }
}
