namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="AppDialogDecision"/>.
/// </summary>
public sealed class AppDialogDecisionTests
{
    /// <summary>
    /// While the application is exiting no dialog opens, whatever else is open.
    /// </summary>
    /// <param name="exitConfirmOpen">Whether the exit confirmation is open.</param>
    /// <param name="dialogOpen">Whether a settings or about dialog is open.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Decide_Exiting_Skips(bool exitConfirmOpen, bool dialogOpen)
    {
        var action = AppDialogDecision.Decide(true, exitConfirmOpen, dialogOpen);

        Assert.Equal(AppDialogAction.Skip, action);
    }

    /// <summary>
    /// An open exit confirmation is brought forward instead of opening or raising another dialog.
    /// </summary>
    /// <param name="dialogOpen">Whether a settings or about dialog is open.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decide_ExitConfirmOpen_BringsConfirmationForward(bool dialogOpen)
    {
        var action = AppDialogDecision.Decide(false, true, dialogOpen);

        Assert.Equal(AppDialogAction.BringExitConfirmForward, action);
    }

    /// <summary>
    /// A second settings/about request brings the open dialog forward.
    /// </summary>
    [Fact]
    public void Decide_DialogOpen_BringsOpenDialogForward()
    {
        var action = AppDialogDecision.Decide(false, false, true);

        Assert.Equal(AppDialogAction.BringOpenForward, action);
    }

    /// <summary>
    /// With nothing open and no exit under way, the dialog opens.
    /// </summary>
    [Fact]
    public void Decide_NothingOpen_Opens()
    {
        var action = AppDialogDecision.Decide(false, false, false);

        Assert.Equal(AppDialogAction.Open, action);
    }
}
