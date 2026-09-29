using ManagedDrive.App.ViewModels;

namespace ManagedDrive.Tests;

public sealed class BusyOverlayViewModelTests
{
    [Fact]
    public void Start_WithoutCancellationSource_CanCancelIsFalse()
    {
        var overlay = new BusyOverlayViewModel();

        overlay.Start("Working...");

        Assert.False(overlay.CanCancel);
    }

    [Fact]
    public void Start_WithCancellationSource_CanCancelIsTrue()
    {
        var overlay = new BusyOverlayViewModel();
        using var cts = new CancellationTokenSource();

        overlay.Start("Working...", cancellationSource: cts);

        Assert.True(overlay.CanCancel);
    }

    [Fact]
    public void CancelCommand_Execute_CancelsTheSource()
    {
        var overlay = new BusyOverlayViewModel();
        using var cts = new CancellationTokenSource();
        overlay.Start("Working...", cancellationSource: cts);

        overlay.CancelCommand.Execute(null);

        Assert.True(cts.IsCancellationRequested);
        Assert.True(overlay.IsCancellationRequested);
    }

    [Fact]
    public void CancelCommand_ExecuteTwice_CancelsOnlyOnce()
    {
        var overlay = new BusyOverlayViewModel();
        var cancelCount = 0;
        using var cts = new CancellationTokenSource();
        cts.Token.Register(() => cancelCount++);
        overlay.Start("Working...", cancellationSource: cts);

        overlay.CancelCommand.Execute(null);
        overlay.CancelCommand.Execute(null);

        Assert.Equal(1, cancelCount);
    }

    [Fact]
    public void CancelCommand_NoCancellationSource_DoesNothing()
    {
        var overlay = new BusyOverlayViewModel();
        overlay.Start("Working...");

        overlay.CancelCommand.Execute(null);

        Assert.False(overlay.IsCancellationRequested);
    }

    [Fact]
    public void Stop_ResetsCanCancelForNextOperation()
    {
        var overlay = new BusyOverlayViewModel();
        using var cts = new CancellationTokenSource();
        overlay.Start("Working...", cancellationSource: cts);

        overlay.Stop();

        Assert.False(overlay.CanCancel);
    }

    [Fact]
    public void Start_AfterPreviousOperationWithoutCancellation_ResetsIsCancellationRequested()
    {
        var overlay = new BusyOverlayViewModel();
        using var cts1 = new CancellationTokenSource();
        overlay.Start("First...", cancellationSource: cts1);
        overlay.CancelCommand.Execute(null);
        overlay.Stop();

        using var cts2 = new CancellationTokenSource();
        overlay.Start("Second...", cancellationSource: cts2);

        Assert.False(overlay.IsCancellationRequested);
    }

    /// <summary>
    /// A second operation must not take over the overlay while the first one still shows it.
    /// </summary>
    [Fact]
    public void TryStart_WhileAnotherOperationIsShown_ReturnsFalseAndKeepsTheFirstOperation()
    {
        var overlay = new BusyOverlayViewModel();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        overlay.Start("First...", cancellationSource: first);

        var started = overlay.TryStart("Second...", cancellationSource: second);

        Assert.False(started);
        Assert.Equal("First...", overlay.StatusText);
        overlay.CancelCommand.Execute(null);
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
    }

    /// <summary>
    /// An idle overlay starts the operation.
    /// </summary>
    [Fact]
    public void TryStart_WhenIdle_ReturnsTrueAndShowsTheOperation()
    {
        var overlay = new BusyOverlayViewModel();

        var started = overlay.TryStart("Working...");

        Assert.True(started);
        Assert.True(overlay.IsBusy);
        Assert.Equal("Working...", overlay.StatusText);
    }

    /// <summary>
    /// Once the previous operation has stopped, the next one can take the overlay.
    /// </summary>
    [Fact]
    public void TryStart_AfterThePreviousOperationStopped_ReturnsTrue()
    {
        var overlay = new BusyOverlayViewModel();
        overlay.Start("First...");
        overlay.Stop();

        var started = overlay.TryStart("Second...");

        Assert.True(started);
        Assert.Equal("Second...", overlay.StatusText);
    }
}
