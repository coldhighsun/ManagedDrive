using ManagedDrive.Cli.Core;
using ManagedDrive.HelperProtocol;
using System.IO.Pipes;

namespace ManagedDrive.App.Cli;

/// <summary>
/// Hosts a named-pipe server on a background task so a second CLI invocation of
/// ManagedDrive.exe can forward its command to the already-running tray instance instead of
/// starting a duplicate process. Commands are dispatched onto the WPF UI thread so
/// <see cref="MainViewModel"/>'s bound <c>Disks</c> collection can be safely updated. Delegates
/// parsing/execution to <c>ManagedDrive.Cli.Core</c>'s <see cref="CliCommandProcessor"/> via the
/// <see cref="ICliDiskController"/> abstraction.
/// </summary>
public sealed class CliPipeServer(MainViewModel mainViewModel) : IDisposable
{
    /// <summary>
    /// Upper bound on reading the request line and writing the response line of a single
    /// connection — not on the command execution in between, which may legitimately take a while
    /// (e.g. exporting a large disk) and is bounded instead by the client's own read timeout. Guards
    /// the single-instance accept loop against a connected client that never sends anything (or
    /// never reads the reply), which would otherwise wedge every other local CLI invocation behind
    /// it until this process exits.
    /// </summary>
    private static readonly TimeSpan PerIoTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on command execution itself. The accept loop serves one connection at a time,
    /// so a command that never returns (e.g. it's dispatched onto the UI thread while that thread
    /// is blocked showing a modal dialog, such as the password prompt in
    /// <c>MountWithPasswordRetryAsync</c>) would otherwise wedge every subsequent CLI invocation
    /// behind it indefinitely, with no diagnostic. Generous enough not to cut off a legitimately
    /// slow command (e.g. exporting a large disk).
    /// </summary>
    private static readonly TimeSpan CommandExecutionTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Logger for faults observed after a command's execution already timed out (see
    /// <see cref="CommandExecutionTimeout"/>) — by that point the client has already been told the
    /// command failed, so a later exception has nowhere else to surface.
    /// </summary>
    private static readonly ILogger Logger = AppLog.CreateLogger<CliPipeServer>();

    private readonly CancellationTokenSource _cts = new();
    private readonly ICliDiskController _diskController = new MainViewModelCliDiskController(mainViewModel);
    private Task? _acceptLoop;
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _cts.Cancel();

        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort shutdown; the accept loop may already be tearing down.
        }

        _cts.Dispose();
    }

    /// <summary>
    /// Starts accepting connections on a background task. Safe to call once; the loop keeps
    /// running (one connection at a time) until <see cref="Dispose"/> is called.
    /// </summary>
    public void Start()
    {
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                CliPipeProtocol.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await HandleConnectionAsync(pipe, ct);
            }
            catch
            {
                // Best-effort — a malformed or interrupted request must not take down the
                // accept loop for future CLI invocations.
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true);
        writer.AutoFlush = true;

        // Either the connected client never sent a request within PerIoTimeout, or the accept
        // loop itself is shutting down — either way, drop this connection without blocking the
        // next one.
        var requestJson = await PipeIo.ReadLineWithTimeoutAsync(reader, PerIoTimeout, ct);
        if (requestJson == null)
        {
            return;
        }

        var args = CliPipeProtocol.DeserializeRequest(requestJson);

        // Marshal onto the UI thread: CliCommandProcessor calls into MainViewModel (via
        // _diskController), which mutates the WPF-bound Disks collection and must not be
        // touched from this pipe thread. Not subject to PerIoTimeout — command execution itself
        // can legitimately run long — but still bounded by CommandExecutionTimeout so a command
        // stuck behind a blocked UI thread can't wedge every later CLI invocation behind it forever.
        var executeTask = Application.Current.Dispatcher.InvokeAsync(
            () => CliCommandProcessor.ExecuteAsync(args, _diskController)).Task.Unwrap();

        CliOutcome result;
        try
        {
            result = await executeTask.WaitAsync(CommandExecutionTimeout, ct);
        }
        catch (TimeoutException)
        {
            // executeTask is still running on the UI dispatcher and is left to finish on its own
            // — there's no cancellation token to thread into it. Observe its eventual completion
            // anyway so a later exception doesn't become an unobserved task exception, and log it
            // since it now happens after this response already told the client it failed.
            _ = executeTask.ContinueWith(
                t => Logger.LogError(t.Exception, "CLI command execution failed after its client was already told it timed out."),
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

            await PipeIo.WriteLineWithTimeoutAsync(
                writer,
                CliPipeProtocol.SerializeResponse(new(
                    false,
                    "Timed out waiting for ManagedDrive to execute the command — it may be blocked on a dialog (e.g. a password prompt) in the app's window.",
                    null,
                    1)),
                PerIoTimeout,
                ct);
            return;
        }

        await PipeIo.WriteLineWithTimeoutAsync(
            writer,
            CliPipeProtocol.SerializeResponse(new(result.Success, result.Message, result.Disks, result.ExitCode, result.Snapshots, result.Json)),
            PerIoTimeout,
            ct);
    }
}
