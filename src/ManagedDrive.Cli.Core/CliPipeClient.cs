using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ManagedDrive.HelperProtocol;

namespace ManagedDrive.Cli.Core;

/// <summary>
/// Attempts to forward CLI arguments to an already-running ManagedDrive tray instance via the
/// named pipe hosted by the app layer's CLI pipe server.
/// </summary>
public static class CliPipeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Upper bound on waiting for the running instance's response line. This is a deadlock guard,
    /// not a throttle on legitimately slow commands (e.g. exporting a large disk) — the server
    /// dispatches the command onto the UI thread and awaits it there before writing back, so a
    /// generous ceiling avoids cutting off a real (if slow) response while still bounding how long
    /// a wedged instance can hang the CLI. The same bound separately applies to writing the
    /// request: the server's pipe has no inbound buffer, so the write itself only completes once
    /// the instance reads it, and a connected-but-wedged instance would otherwise block the write
    /// forever. Overridable by tests via <see cref="TestReadTimeoutOverride"/> to exercise the
    /// timeout paths without a real 5-minute wait.
    /// </summary>
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Test-only override for <see cref="DefaultReadTimeout"/>; <see langword="null"/> means use
    /// the production default. Set via <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static TimeSpan? TestReadTimeoutOverride;

    private static TimeSpan ReadTimeout => TestReadTimeoutOverride ?? DefaultReadTimeout;

    /// <summary>
    /// Test-only override for the pipe name <see cref="TrySend"/> connects to; <see langword="null"/>
    /// means use <see cref="CliPipeProtocol.PipeName"/>. Lets tests stand up a fake server without
    /// colliding with a real running instance's pipe. Set via
    /// <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static string? TestPipeNameOverride;

    private static string PipeName => TestPipeNameOverride ?? CliPipeProtocol.PipeName;

    /// <summary>
    /// Test-only override for the user a server pipe must be owned by (see
    /// <see cref="CurrentUserSid"/>); <see langword="null"/> means the current process's user.
    /// Lets tests stand up a pipe owned by "another user" without a second account. Set via
    /// <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static SecurityIdentifier? TestCurrentUserSidOverride;

    /// <summary>
    /// Gets the user whose own server pipe this client trusts, besides a privileged one.
    /// </summary>
    private static SecurityIdentifier? CurrentUserSid
    {
        get
        {
            if (TestCurrentUserSidOverride is not null)
            {
                return TestCurrentUserSidOverride;
            }

            using var identity = WindowsIdentity.GetCurrent();
            return identity.User;
        }
    }

    /// <summary>
    /// Upper bound on the response line read by <see cref="ReadBoundedLineAsync"/>, mirroring
    /// <c>PipeIo.MaxLineLength</c>'s guard against an unbounded buffer — this pipe's server side
    /// already caps its own request read the same way, and the running instance is itself a peer
    /// this client shouldn't trust to always send '\n'.
    /// </summary>
    private const int MaxResponseLineLength = 64 * 1024;

    /// <summary>
    /// Failure message reported when a running instance's pipe denies this process access.
    /// </summary>
    internal const string AccessDeniedMessage =
        "Access to the running ManagedDrive instance was denied. It may be running as administrator or as another user; run mdrive the same way.";

    /// <summary>
    /// Failure message reported when the pipe was created by another (unprivileged) user, so the
    /// request was withheld.
    /// </summary>
    internal const string UntrustedServerMessage =
        "The ManagedDrive CLI pipe belongs to another user, so the command was not sent. Another program may be posing as ManagedDrive.";

    /// <summary>
    /// Failure message reported when the request may have been delivered but writing it, or
    /// waiting for the response, did not complete within <see cref="ReadTimeout"/>.
    /// </summary>
    internal const string NoResponseMessage =
        "ManagedDrive did not respond in time. The command may still be running there; check its result before retrying.";

    /// <summary>
    /// Failure message reported when the request was delivered but the running instance closed
    /// the connection without sending a response line.
    /// </summary>
    internal const string ConnectionClosedMessage =
        "ManagedDrive closed the connection without responding. The command may or may not have run; check its result before retrying.";

    /// <summary>
    /// Failure message reported when the running instance's response line is not a valid
    /// response.
    /// </summary>
    internal const string InvalidResponseMessage = "ManagedDrive returned a response that could not be read.";

    /// <summary>
    /// Tries to connect to a running instance's CLI pipe and execute <paramref name="args"/>
    /// there.
    /// </summary>
    /// <returns>
    /// <c>true</c> once the request has been delivered to a running instance, whether or not an
    /// answer came back: an answer (regardless of the command's own exit code) is returned as-is,
    /// while a write or read timeout, a connection closed before the response, or an unreadable
    /// response is reported as a failure <paramref name="response"/> — the instance may already be
    /// executing the command, so the caller must not resend it and risk running a side-effecting
    /// command twice. Also <c>true</c> if a running instance refused this process access to its
    /// pipe, or the pipe was created by another user and so was never sent the request
    /// (<paramref name="response"/> then carries a failure explaining that). <c>false</c>
    /// only if the request was never delivered — no instance accepted the connection in time, or
    /// the connection broke while the request was being written — so retrying is safe.
    /// </returns>
    public static bool TrySend(string[] args, out CliResponse response)
    {
        response = new(false, string.Empty, null, 1);

        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            pipe.Connect(ConnectTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // An instance is listening but its pipe rejects this process — typically it's running
            // elevated or as another user. Report that as the answer rather than "nothing
            // listening": the caller would otherwise launch a second instance and then time out
            // waiting for a pipe it can never reach.
            response = new(false, AccessDeniedMessage, null, 1);
            return true;
        }

        if (!PipeSecurityRules.IsTrustedOwner(PipeSecurityRules.TryGetOwner(pipe), CurrentUserSid))
        {
            // Any local user can create a pipe under this well-known name while no instance is
            // running, so never hand the arguments (which may include a --password) to a server
            // neither this user nor an administrator created. Reported as an answer, not as
            // "nothing listening": launching the app would not help while the name is taken.
            response = new(false, UntrustedServerMessage, null, 1);
            return true;
        }

        var reader = new StreamReader(pipe, leaveOpen: true);
        var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        // Disposed manually (not via `using`) because the timeout paths below force-close the
        // underlying pipe to unblock a stuck write or read; disposing reader/writer afterwards would throw
        // trying to flush/close a stream on top of an already-closed pipe.
        try
        {
            return TrySendCore(pipe, reader, writer, args, ref response);
        }
        finally
        {
            try { reader.Dispose(); } catch (Exception) { }
            try { writer.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Calls <see cref="TrySend"/> until the request is delivered, <paramref name="timeout"/> has
    /// passed, or <paramref name="keepTrying"/> reports there's no point waiting any longer — for
    /// when the instance is still starting up (or busy serving other clients) and hasn't opened its
    /// pipe yet.
    /// </summary>
    /// <param name="args">The command-line arguments to forward.</param>
    /// <param name="timeout">How long to keep retrying after the first attempt.</param>
    /// <param name="retryInterval">Delay between attempts.</param>
    /// <param name="keepTrying">
    /// Checked after each failed attempt; returning <see langword="false"/> stops retrying (e.g.
    /// once the instance being waited for has exited). <see langword="null"/> retries until
    /// <paramref name="timeout"/>.
    /// </param>
    /// <returns>
    /// The response, as <see cref="TrySend"/> reports it once the request was delivered, or
    /// <see langword="null"/> if it never was.
    /// </returns>
    public static async Task<CliResponse?> SendWithRetryAsync(
        string[] args, TimeSpan timeout, TimeSpan retryInterval, Func<bool>? keepTrying = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (TrySend(args, out var response))
            {
                return response;
            }

            if (elapsed.Elapsed >= timeout || keepTrying?.Invoke() == false)
            {
                return null;
            }

            await Task.Delay(retryInterval);
        }
    }

    /// <summary>
    /// Writes the request over the connected <paramref name="pipe"/> and reads the response. See
    /// <see cref="TrySend"/> for the meaning of the return value; never throws for a pipe that
    /// breaks mid-exchange.
    /// </summary>
    private static bool TrySendCore(
        NamedPipeClientStream pipe, StreamReader reader, StreamWriter writer, string[] args, ref CliResponse response)
    {
        // Bounded like the read below, for the same reason: the server's pipe has no inbound
        // buffer, so this write only completes once the instance actually reads the request, and a
        // connected instance that never reads (e.g. its accept loop is wedged) would otherwise
        // block here forever.
        var writeTask = writer.WriteLineAsync(CliPipeProtocol.SerializeRequest(args, Environment.CurrentDirectory));

        if (Task.WaitAny([writeTask], ReadTimeout) == -1)
        {
            pipe.Dispose();
            ObserveAbandoned(writeTask);

            // Part of the request may already sit in the instance's pipe, where it could still be
            // read and run, so this is reported like a missing response rather than as "not
            // delivered" — a resend could run a side-effecting command twice.
            response = new(false, NoResponseMessage, null, 1);
            return true;
        }

        try
        {
            writeTask.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The instance dropped the connection before taking the request (e.g. it is shutting
            // down), so nothing ran there and the caller may safely retry.
            return false;
        }

        // From here on the request has been delivered: every outcome below returns true so the
        // caller reports it instead of resending the command.

        // Deliberately not `new CancellationTokenSource(ReadTimeout)`: that schedules its Cancel()
        // call on the ThreadPool, whose timer callback can be delayed well past ReadTimeout if the
        // pool is briefly starved (e.g. many parallel tests each blocked in a sync-over-async call
        // like this one). Task.WaitAny blocks this thread with a real kernel-level timeout instead,
        // so the deadline is enforced even under ThreadPool contention.
        using var readCts = new CancellationTokenSource();
        var readTask = ReadBoundedLineAsync(reader, readCts.Token);

        if (Task.WaitAny([readTask], ReadTimeout) == -1)
        {
            readCts.Cancel();

            // Cancelling the token alone isn't enough: a pending overlapped read on a named pipe
            // can stay stuck past the CancellationToken if the server never writes or closes its
            // end, so force it to unblock by closing the pipe out from under it.
            pipe.Dispose();

            // Deliberately not waiting for readTask: its completion is delivered through the
            // ThreadPool, so under pool starvation a wait here blocked for seconds past
            // ReadTimeout — the very hang this path exists to bound. Nothing reads the abandoned
            // task's result; just observe its fault so it isn't reported as unobserved.
            ObserveAbandoned(readTask);
            response = new(false, NoResponseMessage, null, 1);
            return true;
        }

        string? responseJson;
        try
        {
            responseJson = readTask.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            responseJson = null;
        }

        if (responseJson == null)
        {
            response = new(false, ConnectionClosedMessage, null, 1);
            return true;
        }

        try
        {
            response = CliPipeProtocol.DeserializeResponse(responseJson);
        }
        catch (JsonException)
        {
            response = new(false, InvalidResponseMessage, null, 1);
        }

        return true;
    }

    /// <summary>
    /// Observes the fault of a pipe I/O task this client stopped waiting for (after force-closing
    /// the pipe under it), so the exception it eventually completes with isn't reported as
    /// unobserved.
    /// </summary>
    /// <param name="task">The abandoned write or read task.</param>
    private static void ObserveAbandoned(Task task) =>
        task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Bounded alternative to <see cref="TextReader.ReadLineAsync()"/>: caps the buffered line at
    /// <see cref="MaxResponseLineLength"/> so a misbehaving or compromised app instance on the
    /// other end of this pipe can't make this client buffer an unbounded amount of data by never
    /// sending '\n' — the same DoS shape <c>PipeIo.ReadBoundedLineAsync</c> closes on the listening
    /// side.
    /// </summary>
    /// <param name="reader">The reader to read a line from.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    private static async Task<string?> ReadBoundedLineAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var charBuffer = new char[1];
        var line = new StringBuilder();

        while (true)
        {
            var read = await reader.ReadAsync(charBuffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return line.Length > 0 ? line.ToString() : null;
            }

            var ch = charBuffer[0];
            if (ch is '\n' or '\r')
            {
                return line.ToString();
            }

            if (line.Length >= MaxResponseLineLength)
            {
                return null;
            }

            line.Append(ch);
        }
    }
}
