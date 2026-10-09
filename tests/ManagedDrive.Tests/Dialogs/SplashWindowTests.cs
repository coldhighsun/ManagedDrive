using ManagedDrive.App.Views;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests the window-message handling of <see cref="SplashWindow"/> that keeps a click from
/// activating it.
/// </summary>
public sealed class SplashWindowTests
{
    /// <summary>
    /// A mouse activation request is answered with "do not activate" and marked handled.
    /// </summary>
    [Fact]
    public void WndProc_MouseActivate_ReturnsNoActivateAndHandles()
    {
        var handled = false;

        var result = SplashWindow.WndProc(0, SplashWindow.WmMouseActivate, 0, 0, ref handled);

        Assert.Equal(SplashWindow.MaNoActivate, (int)result);
        Assert.True(handled);
    }

    /// <summary>
    /// Any other message is left to WPF: not handled, zero result.
    /// </summary>
    [Theory]
    [InlineData(0x0201)] // WM_LBUTTONDOWN
    [InlineData(0x0006)] // WM_ACTIVATE
    [InlineData(0x0000)]
    public void WndProc_OtherMessage_LeavesItUnhandled(int message)
    {
        var handled = false;

        var result = SplashWindow.WndProc(0, message, 0, 0, ref handled);

        Assert.Equal(0, (int)result);
        Assert.False(handled);
    }
}
