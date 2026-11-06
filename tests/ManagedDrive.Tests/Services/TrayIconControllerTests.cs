using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests the layout rules of <see cref="TrayIconController"/>'s context menu.
/// </summary>
public sealed class TrayIconControllerTests
{
    /// <summary>
    /// Verifies that the item padding is 4 px at 96 DPI and grows with the DPI, because WinForms does
    /// not scale a padding assigned at runtime.
    /// </summary>
    [Theory]
    [InlineData(96, 4)]
    [InlineData(120, 5)]
    [InlineData(144, 6)]
    [InlineData(192, 8)]
    public void GetItemPadding_DeviceDpi_ScalesAllSides(int deviceDpi, int expected)
    {
        // Act
        var padding = TrayIconController.GetItemPadding(deviceDpi);

        // Assert
        Assert.Equal(expected, padding.Left);
        Assert.Equal(expected, padding.Top);
        Assert.Equal(expected, padding.Right);
        Assert.Equal(expected, padding.Bottom);
    }

    /// <summary>
    /// Verifies that the item margin is 8 px left and right at 96 DPI, nothing above or below, and
    /// grows with the DPI like the padding does.
    /// </summary>
    [Theory]
    [InlineData(96, 8)]
    [InlineData(120, 10)]
    [InlineData(144, 12)]
    [InlineData(192, 16)]
    public void GetItemMargin_DeviceDpi_ScalesLeftAndRightOnly(int deviceDpi, int expected)
    {
        // Act
        var margin = TrayIconController.GetItemMargin(deviceDpi);

        // Assert
        Assert.Equal(expected, margin.Left);
        Assert.Equal(expected, margin.Right);
        Assert.Equal(0, margin.Top);
        Assert.Equal(0, margin.Bottom);
    }
}
