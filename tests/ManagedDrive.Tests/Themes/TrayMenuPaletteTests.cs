using System.Drawing;
using ManagedDrive.App.Themes;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="TrayMenuPalette"/>, the colors of the tray menu.
/// </summary>
public sealed class TrayMenuPaletteTests
{
    /// <summary>
    /// Verifies that an opaque color is not changed by blending.
    /// </summary>
    [Fact]
    public void Blend_OpaqueTop_ReturnsTop()
    {
        // Act
        var result = TrayMenuPalette.Blend(Color.FromArgb(0xFF, 10, 20, 30), Color.White);

        // Assert
        Assert.Equal(Color.FromArgb(0xFF, 10, 20, 30), result);
    }

    /// <summary>
    /// Verifies that a fully transparent color leaves the color under it.
    /// </summary>
    [Fact]
    public void Blend_TransparentTop_ReturnsUnder()
    {
        // Act
        var result = TrayMenuPalette.Blend(Color.FromArgb(0, 255, 0, 0), Color.FromArgb(0xFF, 40, 50, 60));

        // Assert
        Assert.Equal(Color.FromArgb(0xFF, 40, 50, 60), result);
    }

    /// <summary>
    /// Verifies that a half-transparent white over black comes out mid-grey and opaque.
    /// </summary>
    [Fact]
    public void Blend_HalfTransparentWhiteOverBlack_ReturnsMidGrey()
    {
        // Act
        var result = TrayMenuPalette.Blend(Color.FromArgb(0x80, 255, 255, 255), Color.Black);

        // Assert
        Assert.Equal(0xFF, result.A);
        Assert.InRange(result.R, 127, 129);
        Assert.Equal(result.R, result.G);
        Assert.Equal(result.R, result.B);
    }

    /// <summary>
    /// Verifies that the plain dark and light palettes differ in background and foreground.
    /// </summary>
    [Fact]
    public void GetDefault_DarkAndLight_DifferInBackgroundAndForeground()
    {
        // Act
        var dark = TrayMenuPalette.GetDefault(isDark: true);
        var light = TrayMenuPalette.GetDefault(isDark: false);

        // Assert
        Assert.NotEqual(dark.Background, light.Background);
        Assert.NotEqual(dark.Foreground, light.Foreground);
    }
}
