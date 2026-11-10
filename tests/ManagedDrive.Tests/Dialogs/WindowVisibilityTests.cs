namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="WindowVisibility"/>.
/// </summary>
public sealed class WindowVisibilityTests
{
    /// <summary>
    /// A shown window counts as in front of the user unless it is minimized to the taskbar.
    /// </summary>
    /// <param name="isVisible">The window's <c>IsVisible</c>.</param>
    /// <param name="state">The window's state.</param>
    /// <param name="expected">The expected result.</param>
    [Theory]
    [InlineData(true, System.Windows.WindowState.Normal, true)]
    [InlineData(true, System.Windows.WindowState.Maximized, true)]
    [InlineData(true, System.Windows.WindowState.Minimized, false)]
    [InlineData(false, System.Windows.WindowState.Normal, false)]
    public void IsShownToUser_VisibilityAndState_ReturnsWhetherTheContentCanBeSeen(
        bool isVisible, System.Windows.WindowState state, bool expected)
    {
        var shown = WindowVisibility.IsShownToUser(isVisible, state);

        Assert.Equal(expected, shown);
    }

    /// <summary>
    /// No window at all is never shown.
    /// </summary>
    [Fact]
    public void IsShownToUser_NullWindow_ReturnsFalse()
    {
        var shown = WindowVisibility.IsShownToUser(null);

        Assert.False(shown);
    }

    /// <summary>
    /// A dialog centers on its owner when it has one and on the screen otherwise.
    /// </summary>
    /// <param name="hasOwner">Whether the dialog has a shown owner.</param>
    /// <param name="expected">The expected startup location.</param>
    [Theory]
    [InlineData(true, System.Windows.WindowStartupLocation.CenterOwner)]
    [InlineData(false, System.Windows.WindowStartupLocation.CenterScreen)]
    public void GetStartupLocation_OwnerPresence_ReturnsMatchingLocation(
        bool hasOwner, System.Windows.WindowStartupLocation expected)
    {
        var location = WindowVisibility.GetStartupLocation(hasOwner);

        Assert.Equal(expected, location);
    }

    /// <summary>
    /// An open application dialog wins over the main window, so a new dialog stacks on top of it.
    /// </summary>
    [Fact]
    public void PickOwner_DialogOpenAndMainShown_ReturnsDialog()
    {
        var dialog = new object();
        var main = new object();

        var owner = WindowVisibility.PickOwner(dialog, true, main);

        Assert.Same(dialog, owner);
    }

    /// <summary>
    /// A minimized dialog must not own the new one (it would be hidden with it), so the shown main
    /// window is used instead.
    /// </summary>
    [Fact]
    public void PickOwner_DialogMinimizedAndMainShown_ReturnsMainWindow()
    {
        var dialog = new object();
        var main = new object();

        var owner = WindowVisibility.PickOwner(dialog, false, main);

        Assert.Same(main, owner);
    }

    /// <summary>
    /// A minimized dialog with the main window hidden leaves the new dialog ownerless.
    /// </summary>
    [Fact]
    public void PickOwner_DialogMinimizedAndMainHidden_ReturnsNull()
    {
        var owner = WindowVisibility.PickOwner(new object(), false, null);

        Assert.Null(owner);
    }

    /// <summary>
    /// With no dialog open the shown main window owns the dialog.
    /// </summary>
    [Fact]
    public void PickOwner_NoDialogAndMainShown_ReturnsMainWindow()
    {
        var main = new object();

        var owner = WindowVisibility.PickOwner<object>(null, false, main);

        Assert.Same(main, owner);
    }

    /// <summary>
    /// With nothing open and the main window hidden the dialog has no owner.
    /// </summary>
    [Fact]
    public void PickOwner_NoDialogAndMainHidden_ReturnsNull()
    {
        var owner = WindowVisibility.PickOwner<object>(null, false, null);

        Assert.Null(owner);
    }

    /// <summary>
    /// Only a shown window that is not the splash can own a dialog.
    /// </summary>
    /// <param name="isShown">Whether the window is shown.</param>
    /// <param name="isSplash">Whether the window is the splash.</param>
    /// <param name="expected">The expected result.</param>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void IsOwnerCandidate_ShownAndSplash_AllowsOnlyShownNonSplash(bool isShown, bool isSplash, bool expected)
    {
        var result = WindowVisibility.IsOwnerCandidate(isShown, isSplash);

        Assert.Equal(expected, result);
    }
}
