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
}
