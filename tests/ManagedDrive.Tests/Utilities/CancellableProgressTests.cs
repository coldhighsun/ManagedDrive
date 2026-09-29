namespace ManagedDrive.Tests;

public sealed class CancellableProgressTests
{
    [Fact]
    public void Wrap_TokenCannotBeCanceled_ReturnsOriginalInstance()
    {
        var inner = new Progress<double>();

        var wrapped = CancellableProgress.Wrap(inner, CancellationToken.None);

        Assert.Same(inner, wrapped);
    }

    [Fact]
    public void Wrap_NullProgressAndCancelableToken_ReturnsNonNullWrapper()
    {
        using var cts = new CancellationTokenSource();

        var wrapped = CancellableProgress.Wrap(null, cts.Token);

        Assert.NotNull(wrapped);
    }

    [Fact]
    public void Report_TokenNotCanceled_ForwardsToInner()
    {
        using var cts = new CancellationTokenSource();
        var reported = new List<double>();
        var inner = new DelegateProgress(reported.Add);

        var wrapped = CancellableProgress.Wrap(inner, cts.Token)!;
        wrapped.Report(0.5);

        Assert.Equal([0.5], reported);
    }

    [Fact]
    public void Report_TokenCanceled_ThrowsAndDoesNotForwardToInner()
    {
        using var cts = new CancellationTokenSource();
        var reported = new List<double>();
        var inner = new DelegateProgress(reported.Add);
        var wrapped = CancellableProgress.Wrap(inner, cts.Token)!;
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => wrapped.Report(0.5));
        Assert.Empty(reported);
    }

    private sealed class DelegateProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
