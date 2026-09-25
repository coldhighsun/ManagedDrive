using System.Collections.Concurrent;
using System.IO.Pipes;

namespace ManagedDrive.HelperProtocol;

/// <summary>
/// Accepts connections on a named pipe and serves each connected client on its own task, so a
/// slow or stalled client never keeps the next one from connecting. Shared by the app's CLI pipe
/// server (<c>CliPipeServer</c>) and the SYSTEM helper service's pipe server
/// (<c>HelperPipeService</c>).
/// </summary>
public sealed class PipeListener
{
    /// <summary>
    /// Creates a listening pipe instance; see the constructor.
    /// </summary>
    private readonly Func<bool, NamedPipeServerStream> _createPipe;

    /// <summary>
    /// Serves one connected client; see the constructor.
    /// </summary>
    private readonly Func<NamedPipeServerStream, CancellationToken, Task> _serveAsync;

    /// <summary>
    /// Reports a failure to create a pipe instance, which is retried after
    /// <see cref="_retryDelay"/>.
    /// </summary>
    private readonly Action<Exception> _onCreateFailed;

    /// <summary>
    /// Reports a failure accepting or serving a client.
    /// </summary>
    private readonly Action<Exception> _onConnectionFailed;

    /// <summary>
    /// Pause before retrying after a pipe instance couldn't be created, so a persistent failure
    /// doesn't turn the accept loop into a busy spin.
    /// </summary>
    private readonly TimeSpan _retryDelay;

    /// <summary>
    /// Free pipe-instance slots, out of the most instances allowed at once. A slot is taken
    /// before an instance is created and given back once it's disposed.
    /// </summary>
    private readonly SemaphoreSlim _instanceSlots;

    /// <summary>
    /// Connections being served, so <see cref="RunAsync"/> can wait for them to wind down.
    /// </summary>
    private readonly ConcurrentDictionary<Task, byte> _connections = new();

    /// <summary>
    /// Number of connected pipe instances not yet disposed. Zero means no instance of the pipe
    /// exists besides the one about to be created.
    /// </summary>
    private int _connectedInstances;

    /// <summary>
    /// Initializes a listener.
    /// </summary>
    /// <param name="maxInstances">
    /// Most pipe instances alive at once — one listening for the next client, the others serving
    /// connected clients; must match the instance limit <paramref name="createPipe"/> creates the
    /// pipe with.
    /// </param>
    /// <param name="createPipe">
    /// Creates a listening pipe instance. Its argument tells whether no other instance of this
    /// listener's pipe exists; the instance should then be created with
    /// <see cref="PipeOptions.FirstPipeInstance"/>, so it fails if another process already holds
    /// the name instead of joining that process's pipe, which would let it take over some of the
    /// clients.
    /// </param>
    /// <param name="serveAsync">
    /// Serves one connected client. The pipe is disposed once the returned task completes.
    /// </param>
    /// <param name="onCreateFailed">
    /// Reports a failure to create a pipe instance (e.g. another process holds the name).
    /// </param>
    /// <param name="onConnectionFailed">
    /// Reports a failure accepting or serving a client (e.g. it hung up early).
    /// </param>
    /// <param name="retryDelay">Pause before retrying after a pipe instance couldn't be created.</param>
    public PipeListener(
        int maxInstances,
        Func<bool, NamedPipeServerStream> createPipe,
        Func<NamedPipeServerStream, CancellationToken, Task> serveAsync,
        Action<Exception> onCreateFailed,
        Action<Exception> onConnectionFailed,
        TimeSpan retryDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInstances, 1);

        _createPipe = createPipe;
        _serveAsync = serveAsync;
        _onCreateFailed = onCreateFailed;
        _onConnectionFailed = onConnectionFailed;
        _retryDelay = retryDelay;
        _instanceSlots = new(maxInstances, maxInstances);
    }

    /// <summary>
    /// Accepts and serves clients until <paramref name="ct"/> is cancelled, then waits for the
    /// clients still being served (whose serving is cancelled too) to wind down. Never throws.
    /// </summary>
    /// <param name="ct">Stops the listener.</param>
    /// <returns>A task that completes once the listener and every connection have stopped.</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await AcceptLoopAsync(ct);
        }
        finally
        {
            // Connection tasks never fault; see ServeConnectionAsync.
            await Task.WhenAll(_connections.Keys);
        }
    }

    /// <summary>
    /// Keeps a pipe instance listening until <paramref name="ct"/> is cancelled, handing each
    /// connected client off to its own task so the next client can connect right away.
    /// </summary>
    /// <param name="ct">Stops the loop.</param>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Every slot taken means as many clients are connected as the pipe allows; wait for
            // one to finish rather than fail creating an instance the pipe has no room for.
            try
            {
                await _instanceSlots.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            NamedPipeServerStream pipe;
            try
            {
                // While a connected instance is alive nobody else can create an instance under the
                // name (the pipe's DACL must not admit them), so only a pipe created with no other
                // instance alive needs to claim the name. A connection closing between this check
                // and the creation leaves a vanishingly small window in which the name could be
                // taken over; clients checking the pipe's owner still cover it.
                pipe = _createPipe(Volatile.Read(ref _connectedInstances) == 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _instanceSlots.Release();
                _onCreateFailed(ex);
                if (!await DelayUnlessCancelledAsync(_retryDelay, ct))
                {
                    return;
                }

                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                await pipe.DisposeAsync();
                _instanceSlots.Release();
                if (ex is OperationCanceledException)
                {
                    return;
                }

                // A client that connected and hung up before the connection was accepted. Not
                // worth a pause: it's specific to that client, and pausing would let one that keeps
                // doing it hold off every other client.
                _onConnectionFailed(ex);
                continue;
            }

            Interlocked.Increment(ref _connectedInstances);

            // Task.Run so a serve delegate doing synchronous work before its first await can't
            // hold up the accept loop. Deliberately without ct: the task must run to release the
            // pipe and its slot even if cancellation comes first.
            var connection = Task.Run(() => ServeConnectionAsync(pipe, ct), CancellationToken.None);
            _connections.TryAdd(connection, 0);
            _ = connection.ContinueWith(
                t => _connections.TryRemove(t, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Serves one connected client, then disposes its pipe instance and frees its slot. Never
    /// throws: a malformed or interrupted request must not take down the listener.
    /// </summary>
    /// <param name="pipe">The connected pipe instance, owned by this method from now on.</param>
    /// <param name="ct">Stops serving the client.</param>
    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await using (pipe)
            {
                await _serveAsync(pipe, ct);
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _onConnectionFailed(ex);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            // Only once the instance is disposed: _connectedInstances reaching zero must mean no
            // instance of the pipe is left.
            Interlocked.Decrement(ref _connectedInstances);
            _instanceSlots.Release();
        }
    }

    /// <summary>
    /// Waits <paramref name="delay"/>, returning <see langword="false"/> instead of throwing if
    /// <paramref name="ct"/> is cancelled first.
    /// </summary>
    /// <param name="delay">How long to wait.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns><see langword="true"/> if the full delay elapsed.</returns>
    private static async Task<bool> DelayUnlessCancelledAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
