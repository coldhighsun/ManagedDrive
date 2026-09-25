using ManagedDrive.Cli.Core;
using ManagedDrive.HelperProtocol;
using System.IO.Pipes;
using System.Security.Principal;
using ThrottledLogging;

namespace ManagedDrive.App.Cli;

/// <summary>
/// Hosts a named-pipe server on a background task so a second CLI invocation of
/// ManagedDrive.exe can forward its command to the already-running tray instance instead of
/// starting a duplicate process. Commands are dispatched onto the WPF UI thread so
/// <see cref="MainViewModel"/>'s bound <c>Disks</c> collection can be safely updated. Delegates
/// parsing/execution to <c>ManagedDrive.Cli.Core</c>'s <see cref="CliCommandProcessor"/> via the
/// <see cref="ICliDiskController"/> abstraction. Several clients may be connected at once, but
/// their commands run one at a time.
/// </summary>
public sealed class CliPipeServer : IDisposable
{
    /// <summary>
    /// Most pipe instances alive at once: one listening for the next client, the others serving
    /// connected clients. Lets a CLI invocation connect (and queue its command) while another's
    /// command is still running, instead of failing to connect at all.
    /// </summary>
    internal const int MaxInstances = 4;

    /// <summary>
    /// Upper bound on reading the request line and writing the response line of a single
    /// connection — not on the command execution in between, which may legitimately take a while
    /// (e.g. exporting a large disk) and is bounded instead by the client's own read timeout. Guards
    /// against a connected client that never sends anything (or never reads the reply) holding
    /// one of the <see cref="MaxInstances"/> pipe instances until this process exits.
    /// </summary>
    private static readonly TimeSpan PerIoTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on command execution itself. Commands run one at a time, so a command that
    /// never returns (e.g. it's dispatched onto the UI thread while that thread is blocked showing
    /// a modal dialog, such as the password prompt in <c>MountWithPasswordRetryAsync</c>) would
    /// otherwise wedge every subsequent CLI invocation behind it indefinitely, with no diagnostic.
    /// Generous enough not to cut off a legitimately slow command (e.g. exporting a large disk).
    /// </summary>
    private static readonly TimeSpan CommandExecutionTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default upper bound on a command waiting for the one before it to finish. Past it the
    /// command is refused without running, rather than started so late that its client has
    /// likely given up waiting for the response.
    /// </summary>
    private static readonly TimeSpan DefaultExecutionQueueTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Failure message sent when a command was refused because another one was still running
    /// after <see cref="_executionQueueTimeout"/>.
    /// </summary>
    internal const string BusyMessage =
        "ManagedDrive is still busy with another command, so this one was not run. Try again once it finishes.";

    /// <summary>
    /// Logger for faults observed after a command's execution already timed out (see
    /// <see cref="CommandExecutionTimeout"/>) — by that point the client has already been told the
    /// command failed, so a later exception has nowhere else to surface.
    /// </summary>
    private static readonly ILogger Logger = AppLog.CreateLogger<CliPipeServer>();

    /// <summary>
    /// Pause before retrying after the pipe couldn't be created, so a persistent failure doesn't
    /// turn the accept loop into a busy spin.
    /// </summary>
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Cancelled by <see cref="Dispose"/> to stop the listener and every open connection.
    /// </summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Executes a decoded request and returns its outcome.
    /// </summary>
    private readonly Func<CliRequest, Task<CliOutcome>> _execute;

    /// <summary>
    /// Upper bound on a command waiting for the one before it to finish; see
    /// <see cref="DefaultExecutionQueueTimeout"/>.
    /// </summary>
    private readonly TimeSpan _executionQueueTimeout;

    /// <summary>
    /// Held while a command executes, so commands from concurrent clients run one at a time as
    /// they did when the pipe only served one client at a time.
    /// </summary>
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    /// <summary>
    /// Accepts connections and serves each on its own task.
    /// </summary>
    private readonly PipeListener _listener;

    /// <summary>
    /// The listener run started by <see cref="Start"/>.
    /// </summary>
    private Task? _run;

    /// <summary>
    /// Whether <see cref="Dispose"/> has run.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a server that executes commands against <paramref name="mainViewModel"/>.
    /// </summary>
    /// <param name="mainViewModel">The view model whose disks commands operate on.</param>
    public CliPipeServer(MainViewModel mainViewModel)
        : this(CliPipeProtocol.PipeName, CreateUiThreadExecutor(new MainViewModelCliDiskController(mainViewModel)))
    {
    }

    /// <summary>
    /// Initializes a server for <paramref name="pipeName"/> that executes requests with
    /// <paramref name="execute"/>.
    /// </summary>
    /// <param name="pipeName">The name of the pipe to serve.</param>
    /// <param name="execute">Executes a decoded request and returns its outcome.</param>
    /// <param name="executionQueueTimeout">
    /// Upper bound on a command waiting for the one before it, or <c>null</c> for
    /// <see cref="DefaultExecutionQueueTimeout"/>.
    /// </param>
    internal CliPipeServer(string pipeName, Func<CliRequest, Task<CliOutcome>> execute, TimeSpan? executionQueueTimeout = null)
    {
        _execute = execute;
        _executionQueueTimeout = executionQueueTimeout ?? DefaultExecutionQueueTimeout;
        _listener = new(
            MaxInstances,
            firstInstance => CreatePipe(pipeName, firstInstance),
            HandleConnectionAsync,
            ex => Logger.LogWarningThrottled(
                "cli-pipe-create-failed", TimeSpan.FromMinutes(5),
                "Failed to create the CLI pipe (another process may be holding its name); retrying: {Error}", ex.Message),
            ex => Logger.LogDebug(ex, "CLI pipe connection failed."),
            AcceptRetryDelay);
    }

    /// <summary>
    /// Stops accepting connections and waits briefly for open ones to wind down.
    /// </summary>
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
            _run?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort shutdown; the listener may already be tearing down.
        }

        _cts.Dispose();
    }

    /// <summary>
    /// Starts accepting connections on a background task. Safe to call once; the loop keeps
    /// running until <see cref="Dispose"/> is called.
    /// </summary>
    public void Start()
    {
        _run = Task.Run(() => _listener.RunAsync(_cts.Token));
    }

    /// <summary>
    /// Creates the listening end of the CLI pipe. Only this process's token owner may connect —
    /// the user, or <c>BUILTIN\Administrators</c> when elevated, so the same user's unelevated
    /// processes can't drive an elevated instance — and never a remote client.
    /// </summary>
    /// <param name="pipeName">The pipe name.</param>
    /// <param name="firstInstance">
    /// Whether no other instance of this server's pipe exists. The pipe is then created as the
    /// name's first instance, failing if another process already holds the name: joining that
    /// process's pipe would let it take over some of this server's clients.
    /// </param>
    /// <returns>The pipe, waiting for a connection.</returns>
    /// <exception cref="UnauthorizedAccessException">
    /// <paramref name="firstInstance"/> is set and another process already holds the name.
    /// </exception>
    internal static NamedPipeServerStream CreatePipe(string pipeName, bool firstInstance)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.Owner ?? identity.User!;

        var options = firstInstance ? PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance : PipeOptions.Asynchronous;
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            PipeSecurityRules.CreateOwnerOnlySecurity(owner));
    }

    /// <summary>
    /// Creates the production executor: runs each request's command on the WPF UI thread, since
    /// <see cref="CliCommandProcessor"/> calls into <see cref="MainViewModel"/> (via
    /// <paramref name="diskController"/>), which mutates the WPF-bound <c>Disks</c> collection and
    /// must not be touched from a pipe thread.
    /// </summary>
    /// <param name="diskController">The controller commands operate through.</param>
    /// <returns>The executor.</returns>
    private static Func<CliRequest, Task<CliOutcome>> CreateUiThreadExecutor(ICliDiskController diskController) =>
        request => Application.Current.Dispatcher.InvokeAsync(
            () => CliCommandProcessor.ExecuteAsync(request.Args, diskController, request.WorkingDirectory)).Task.Unwrap();

    /// <summary>
    /// Reads one request from <paramref name="pipe"/>, executes it once the commands queued ahead
    /// of it have finished, and writes the response back.
    /// </summary>
    /// <param name="pipe">The connected pipe instance.</param>
    /// <param name="ct">Stops handling the request.</param>
    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true);
        writer.AutoFlush = true;

        // Either the connected client never sent a request within PerIoTimeout, or the server
        // itself is shutting down — either way, drop this connection.
        var requestJson = await PipeIo.ReadLineWithTimeoutAsync(reader, PerIoTimeout, ct);
        if (requestJson == null)
        {
            return;
        }

        var request = CliPipeProtocol.DeserializeRequest(requestJson);

        CliResponse response;
        if (!await _executionGate.WaitAsync(_executionQueueTimeout, ct))
        {
            response = new(false, BusyMessage, null, 1);
        }
        else
        {
            try
            {
                response = await ExecuteAsync(request, ct);
            }
            finally
            {
                _executionGate.Release();
            }
        }

        await PipeIo.WriteLineWithTimeoutAsync(writer, CliPipeProtocol.SerializeResponse(response), PerIoTimeout, ct);
    }

    /// <summary>
    /// Executes <paramref name="request"/> and turns its outcome into the response to send. Not
    /// subject to <see cref="PerIoTimeout"/> — command execution itself can legitimately run long
    /// — but still bounded by <see cref="CommandExecutionTimeout"/> so a command stuck behind a
    /// blocked UI thread can't wedge every later CLI invocation behind it forever.
    /// </summary>
    /// <param name="request">The decoded request.</param>
    /// <param name="ct">Stops waiting for the command.</param>
    /// <returns>The response to send back.</returns>
    private async Task<CliResponse> ExecuteAsync(CliRequest request, CancellationToken ct)
    {
        var executeTask = _execute(request);

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

            return new(
                false,
                "Timed out waiting for ManagedDrive to execute the command — it may be blocked on a dialog (e.g. a password prompt) in the app's window.",
                null,
                1);
        }

        return new(result.Success, result.Message, result.Disks, result.ExitCode, result.Snapshots, result.Json);
    }
}
