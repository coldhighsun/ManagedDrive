using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ManagedDrive.HelperProtocol;

/// <summary>
/// Client used by the user-mode app to ask the SYSTEM helper service to publish or remove a
/// global DOS-device symlink. Every call is best-effort: if the service is not installed or not
/// running, the call fails silently (<c>false</c>) and the caller degrades gracefully — the disk
/// still mounts normally, just without cross-session visibility.
/// </summary>
public static class HelperPipeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(2000);

    /// <summary>
    /// Upper bound on waiting for the service's response line. The service only performs a quick
    /// DOS-device symlink publish/remove, so this is purely a deadlock guard against a wedged or
    /// unresponsive service — without it, a connected-but-silent service would block the caller
    /// forever even though every call is documented as best-effort. Overridable by tests via
    /// <see cref="TestReadTimeoutOverride"/> to exercise the timeout path without a real 5-second
    /// wait.
    /// </summary>
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Test-only override for <see cref="DefaultReadTimeout"/>; <see langword="null"/> means use
    /// the production default. Set via <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static TimeSpan? TestReadTimeoutOverride;

    private static TimeSpan ReadTimeout => TestReadTimeoutOverride ?? DefaultReadTimeout;

    /// <summary>
    /// Test-only override for the pipe name <see cref="TrySend"/> connects to; <see langword="null"/>
    /// means use <see cref="HelperPipeProtocol.PipeName"/>. Lets tests stand up a fake service
    /// without colliding with a real running SYSTEM service's pipe. Set via
    /// <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static string? TestPipeNameOverride;

    private static string PipeName => TestPipeNameOverride ?? HelperPipeProtocol.PipeName;

    /// <summary>
    /// Upper bound on the response line read by <see cref="ReadBoundedLineAsync"/>, mirroring
    /// <c>PipeIo.MaxLineLength</c>'s guard against an unbounded buffer — this pipe's server side
    /// already caps its own request read the same way, and the service is itself a peer this
    /// client shouldn't trust to always send '\n'.
    /// </summary>
    private const int MaxResponseLineLength = 64 * 1024;

    /// <summary>
    /// Asks the service to publish a global symlink <paramref name="letter"/> →
    /// <paramref name="devicePath"/>.
    /// </summary>
    public static bool TryPublish(string letter, string devicePath, out HelperResponse response) =>
        TrySend(new(HelperPipeProtocol.OpPublish, letter, devicePath), out response, out _);

    /// <summary>
    /// Asks the service to remove the global symlink previously published for
    /// <paramref name="letter"/>.
    /// </summary>
    public static bool TryUnpublish(string letter, out HelperResponse response) =>
        TrySend(new(HelperPipeProtocol.OpUnpublish, letter, null), out response, out _);

    /// <summary>
    /// Checks whether the helper service is installed and listening.
    /// </summary>
    public static bool IsServiceAvailable() => IsServiceAvailable(out _);

    /// <summary>
    /// Checks whether the helper service is installed and listening, additionally reporting why
    /// the check failed (e.g. the exception type/message from the failed connect or read) so a
    /// caller can log it — <see cref="TrySend"/> otherwise collapses every failure into a bare
    /// <see langword="false"/>, which makes an unexpectedly-unavailable service undiagnosable.
    /// </summary>
    /// <param name="failureReason">
    /// A short description of why the check failed, or <see langword="null"/> if it succeeded.
    /// </param>
    public static bool IsServiceAvailable(out string? failureReason)
    {
        var ok = TrySend(new(HelperPipeProtocol.OpPing, null, null), out var response, out failureReason);
        return ok && response.Success;
    }

    private static bool TrySend(HelperRequest request, out HelperResponse response, out string? failureReason)
    {
        response = new(false, string.Empty);
        failureReason = null;

        using var pipe = new NamedPipeClientStream(
            ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            // Enough for the service to read who is asking (it authorizes by user); it gets no
            // token it could act as this user with.
            TokenImpersonationLevel.Identification);

        try
        {
            pipe.Connect(ConnectTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            failureReason = $"connect failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        var reader = new StreamReader(pipe, leaveOpen: true);
        var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        // Disposed manually (not via `using`) because the timeout path below force-closes the
        // underlying pipe to unblock a stuck read; disposing reader/writer afterwards would throw
        // trying to flush/close a stream on top of an already-closed pipe.
        try
        {
            return TrySendCore(pipe, reader, writer, request, ref response, ref failureReason);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException)
        {
            // The connect succeeded but the service dropped the connection (broken pipe on the
            // write or read) or answered with a line that isn't a valid response. Every call here
            // is documented as best-effort, so report it like any other failure instead of letting
            // the exception escape into callers that (like the Settings dialog's status check)
            // never expect one.
            failureReason = $"request failed: {ex.GetType().Name}: {ex.Message}";
            response = new(false, string.Empty);
            return false;
        }
        finally
        {
            try { reader.Dispose(); } catch (Exception) { }
            try { writer.Dispose(); } catch (Exception) { }
        }
    }

    private static bool TrySendCore(
        NamedPipeClientStream pipe, StreamReader reader, StreamWriter writer, HelperRequest request,
        ref HelperResponse response, ref string? failureReason)
    {
        writer.WriteLine(HelperPipeProtocol.SerializeRequest(request));

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
            // end, so force it to unblock by closing the pipe out from under it. That's what
            // actually aborts the OS-level read; the token cancellation above is only enough when
            // the read hasn't yet reached the OS. TrySend's finally block disposes reader/writer
            // on top of this already-closed pipe and swallows the resulting exceptions.
            pipe.Dispose();

            // Deliberately not waiting for readTask: its completion is delivered through the
            // ThreadPool, so under pool starvation a wait here blocked for seconds past
            // ReadTimeout — the very hang this path exists to bound. Nothing reads the abandoned
            // task's result; just observe its fault so it isn't reported as unobserved.
            ObserveAbandonedRead(readTask);
            failureReason = $"read timed out after {ReadTimeout}";
            return false;
        }

        var responseJson = readTask.GetAwaiter().GetResult();

        if (responseJson == null)
        {
            failureReason = "connection closed before a response line was received";
            return false;
        }

        response = HelperPipeProtocol.DeserializeResponse(responseJson);
        return true;
    }

    private static void ObserveAbandonedRead(Task readTask) =>
        readTask.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Bounded alternative to <see cref="TextReader.ReadLineAsync()"/>: caps the buffered line at
    /// <see cref="MaxResponseLineLength"/> so a misbehaving or compromised service on the other end
    /// of this pipe can't make this client buffer an unbounded amount of data by never sending
    /// '\n' — the same DoS shape <c>PipeIo.ReadBoundedLineAsync</c> closes on the listening side.
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
