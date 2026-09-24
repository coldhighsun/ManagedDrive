namespace ManagedDrive.HelperProtocol;

/// <summary>
/// Shared per-call timeout wrapper for named-pipe line reads/writes, used by both the app's CLI
/// pipe server (<c>CliPipeServer</c>) and the SYSTEM helper service's pipe server
/// (<c>HelperPipeService</c>) to bound a single connection's request-line read and response-line
/// write. Lives here (rather than duplicated in each) because <c>ManagedDrive.App</c> and
/// <c>ManagedDrive.Service</c> both already reference this dependency-free project.
/// </summary>
public static class PipeIo
{
    /// <summary>
    /// Upper bound on a single line read by <see cref="ReadLineWithTimeoutAsync"/>. Request/response
    /// lines are short control messages (JSON commands), never a data-transfer payload, so this can
    /// stay small while still leaving comfortable headroom — its purpose is only to stop a
    /// connected client from making the reader buffer an unbounded amount of data by never sending
    /// '\n' (this pipe is reachable by any locally authenticated user, including against the
    /// SYSTEM-level helper service, so an unbounded read is a memory-exhaustion DoS, not just a
    /// theoretical concern).
    /// </summary>
    private const int MaxLineLength = 64 * 1024;

    /// <summary>
    /// Reads one line via <paramref name="reader"/>, bounded by <paramref name="timeout"/> on top
    /// of <paramref name="cancellationToken"/>, and by <see cref="MaxLineLength"/> on top of both.
    /// Returns <see langword="null"/> when the stream ends before a full line arrives, when the
    /// read times out or <paramref name="cancellationToken"/> fires, or when the line exceeds
    /// <see cref="MaxLineLength"/> before a newline is seen — callers that only care about "got a
    /// request or not" don't need to distinguish those cases. An actual empty line in the stream
    /// still comes back as <see cref="string.Empty"/>, not <see langword="null"/>.
    /// </summary>
    /// <param name="reader">The reader to read a line from.</param>
    /// <param name="timeout">Upper bound on the read, independent of <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Token that also cancels the read (e.g. server shutdown).</param>
    public static async Task<string?> ReadLineWithTimeoutAsync(
        TextReader reader, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await ReadBoundedLineAsync(reader, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Bounded alternative to <see cref="TextReader.ReadLineAsync()"/>: reads one character at a
    /// time (cheap here — a control-message line, not a data-transfer hot path) so a line can be
    /// rejected as soon as it exceeds <see cref="MaxLineLength"/> without ever buffering more than
    /// that much data, unlike the unbounded internal buffer <see cref="TextReader.ReadLineAsync()"/>
    /// would grow.
    /// </summary>
    private static async Task<string?> ReadBoundedLineAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var charBuffer = new char[1];
        var line = new System.Text.StringBuilder();

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
                // '\r' terminates the line immediately, matching TextReader.ReadLineAsync's
                // contract (a bare '\r' with no following '\n' still ends the line). A '\n' that
                // follows a '\r' is simply never read — harmless, since every caller reads at
                // most one line per connection before disposing the pipe.
                return line.ToString();
            }

            if (line.Length >= MaxLineLength)
            {
                return null;
            }

            line.Append(ch);
        }
    }

    /// <summary>
    /// Writes one line via <paramref name="writer"/>, bounded by <paramref name="timeout"/> on top
    /// of <paramref name="cancellationToken"/>. Swallows a timeout/cancellation rather than
    /// throwing — a caller that already computed its response has nothing more useful to do than
    /// give up on a client that stopped reading.
    /// </summary>
    /// <param name="writer">The writer to write a line to.</param>
    /// <param name="line">The line to write.</param>
    /// <param name="timeout">Upper bound on the write, independent of <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Token that also cancels the write (e.g. server shutdown).</param>
    public static async Task WriteLineWithTimeoutAsync(
        TextWriter writer, string line, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await writer.WriteLineAsync(line.AsMemory(), timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Client stopped reading the reply; nothing more to do.
        }
    }
}
