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
}
