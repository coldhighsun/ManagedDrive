namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Wraps an <see cref="IProgress{T}"/> so that reporting progress also checks a
/// <see cref="CancellationToken"/> and throws <see cref="OperationCanceledException"/> once it's
/// canceled. Long-running Core operations (image load/save, archive extraction/export, snapshot
/// write) already report progress at roughly the granularity a user would want to cancel at (per
/// node/chunk), so checking cancellation at every <see cref="Report"/> call gets responsive
/// cancellation without threading a <see cref="CancellationToken"/> through every stream and loop
/// individually. The cost is that an operation with no (or very coarse) progress reporting — e.g.
/// a disk holding one huge file as a single node — only notices cancellation at the next report,
/// not sooner.
/// </summary>
/// <param name="inner">The progress reporter to forward to, or <see langword="null"/> for none.</param>
/// <param name="cancellationToken">The token checked on every <see cref="Report"/> call.</param>
internal sealed class CancellableProgress(IProgress<double>? inner, CancellationToken cancellationToken) : IProgress<double>
{
    /// <summary>
    /// Wraps <paramref name="progress"/> so reporting also checks <paramref name="cancellationToken"/>,
    /// or returns <paramref name="progress"/> unchanged when the token can never be canceled (the
    /// common case of a caller not requesting cancellation support).
    /// </summary>
    /// <param name="progress">The progress reporter to wrap, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">The cancellation token to check on every report.</param>
    /// <returns>
    /// A cancellation-checking wrapper around <paramref name="progress"/>, or <paramref name="progress"/>
    /// itself when <paramref name="cancellationToken"/>.<see cref="CancellationToken.CanBeCanceled"/> is
    /// <see langword="false"/>.
    /// </returns>
    internal static IProgress<double>? Wrap(IProgress<double>? progress, CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled ? new CancellableProgress(progress, cancellationToken) : progress;

    /// <summary>
    /// Checks <paramref name="cancellationToken"/>, then forwards <paramref name="value"/> to
    /// <paramref name="inner"/> if it's non-null.
    /// </summary>
    /// <param name="value">The progress value to report, in [0, 1].</param>
    public void Report(double value)
    {
        cancellationToken.ThrowIfCancellationRequested();
        inner?.Report(value);
    }
}
