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
    /// Reads one line via <paramref name="reader"/>, bounded by <paramref name="timeout"/> on top
    /// of <paramref name="cancellationToken"/>. Returns <see langword="null"/> both when the
    /// stream ends before a full line arrives (as <see cref="TextReader.ReadLineAsync()"/> would)
    /// and when the read times out or <paramref name="cancellationToken"/> fires — callers that
    /// only care about "got a request or not" don't need to distinguish those cases. An actual
    /// empty line in the stream still comes back as <see cref="string.Empty"/>, not
    /// <see langword="null"/>.
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
            return await reader.ReadLineAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
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
