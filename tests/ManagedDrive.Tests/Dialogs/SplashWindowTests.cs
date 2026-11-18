using ManagedDrive.App.Infrastructure;
using ManagedDrive.App.Views;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests the window-message handling of <see cref="SplashWindow"/> that keeps a click from
/// activating it.
/// </summary>
public sealed class SplashWindowTests
{
    /// <summary>
    /// A window is centered in the primary monitor's work area, including one whose origin is not (0, 0)
    /// because the taskbar sits at the top or left.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 1920, 1040, 680, 340)]
    [InlineData(0, 48, 1920, 1032, 680, 384)]
    [InlineData(64, 0, 1856, 1080, 712, 360)]
    [InlineData(0, 0, 500, 300, 0, 0)]
    [InlineData(10, 20, 560, 200, 10, 20)]
    public void CenterInWorkArea_ForWorkArea_CentersTheWindow(
        double left, double top, double width, double height, double expectedLeft, double expectedTop)
    {
        var (resultLeft, resultTop) = PrimaryMonitorPlacement.CenterInWorkArea(new(left, top, width, height), 560, 360);

        Assert.Equal(expectedLeft, resultLeft);
        Assert.Equal(expectedTop, resultTop);
    }

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
