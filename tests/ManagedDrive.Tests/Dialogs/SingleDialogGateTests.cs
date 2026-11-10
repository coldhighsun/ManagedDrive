namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="SingleDialogGate{TDialog}"/>.
/// </summary>
public sealed class SingleDialogGateTests
{
    /// <summary>
    /// With no dialog open there is nothing to bring forward.
    /// </summary>
    [Fact]
    public void TryBringForward_NoDialogOpen_ReturnsFalse()
    {
        var gate = new SingleDialogGate<object>();
        var called = false;

        var result = gate.TryBringForward(_ => called = true);

        Assert.False(result);
        Assert.False(called);
    }

    /// <summary>
    /// A second request made while the dialog is open reaches that same dialog.
    /// </summary>
    [Fact]
    public void TryBringForward_DialogOpen_BringsOpenDialogForward()
    {
        var gate = new SingleDialogGate<object>();
        var dialog = new object();
        object? broughtForward = null;
        bool? secondRequest = null;

        gate.Show(dialog, _ =>
        {
            secondRequest = gate.TryBringForward(d => broughtForward = d);
            return true;
        });

        Assert.True(secondRequest);
        Assert.Same(dialog, broughtForward);
    }

    /// <summary>
    /// Once the dialog closes, the gate is free again and returns the dialog's result.
    /// </summary>
    [Fact]
    public void Show_DialogClosed_ReturnsResultAndReleasesGate()
    {
        var gate = new SingleDialogGate<object>();

        var result = gate.Show(new object(), _ => true);

        Assert.True(result);
        Assert.False(gate.TryBringForward(_ => { }));
    }

    /// <summary>
    /// A dialog that throws while showing must not block later requests.
    /// </summary>
    [Fact]
    public void Show_ShowThrows_ReleasesGate()
    {
        var gate = new SingleDialogGate<object>();

        Assert.Throws<InvalidOperationException>(
            () => gate.Show(new object(), _ => throw new InvalidOperationException()));

        Assert.False(gate.TryBringForward(_ => { }));
    }

    /// <summary>
    /// The open dialog is exposed while it is shown and cleared once it closes.
    /// </summary>
    [Fact]
    public void Current_DuringShow_ReturnsDialogAndClearsAfterwards()
    {
        var gate = new SingleDialogGate<object>();
        var dialog = new object();
        object? during = null;

        gate.Show(dialog, _ =>
        {
            during = gate.Current;
            return true;
        });

        Assert.Same(dialog, during);
        Assert.Null(gate.Current);
    }

    /// <summary>
    /// A nested Show that finishes must not make the gate forget the outer dialog that is still open.
    /// </summary>
    [Fact]
    public void Show_NestedShowCloses_KeepsOuterDialogTracked()
    {
        var gate = new SingleDialogGate<object>();
        var outer = new object();
        var inner = new object();
        object? afterInner = null;
        object? duringInner = null;

        gate.Show(outer, _ =>
        {
            gate.Show(inner, _ =>
            {
                duringInner = gate.Current;
                return true;
            });
            afterInner = gate.Current;
            return true;
        });

        Assert.Same(inner, duringInner);
        Assert.Same(outer, afterInner);
        Assert.Null(gate.Current);
    }

    /// <summary>
    /// With no dialog open there is nothing to close.
    /// </summary>
    [Fact]
    public void TryClose_NoDialogOpen_ReturnsFalseWithoutClosing()
    {
        var gate = new SingleDialogGate<object>();
        var called = false;

        var result = gate.TryClose(_ => called = true);

        Assert.False(result);
        Assert.False(called);
    }

    /// <summary>
    /// The open dialog is the one that gets closed.
    /// </summary>
    [Fact]
    public void TryClose_DialogOpen_ClosesOpenDialog()
    {
        var gate = new SingleDialogGate<object>();
        var dialog = new object();
        object? closed = null;
        bool? result = null;

        gate.Show(dialog, _ =>
        {
            result = gate.TryClose(d => closed = d);
            return true;
        });

        Assert.True(result);
        Assert.Same(dialog, closed);
    }
}
