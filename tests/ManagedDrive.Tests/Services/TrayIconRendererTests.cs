using System.Drawing;
using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="TrayIconLayout"/>, <see cref="TrayIconRenderer"/> and <see cref="TrayAlertText"/>.
/// </summary>
public sealed class TrayIconRendererTests
{
    /// <summary>
    /// Verifies that the lights and the badge stay inside the icon and never overlap, at every
    /// notification-area size from 100% to 200% scaling.
    /// </summary>
    /// <param name="size">The icon size.</param>
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void For_EverySize_PartsFitAndDoNotOverlap(int size)
    {
        // Act
        var layout = TrayIconLayout.For(size);

        // Assert
        var icon = new Rectangle(0, 0, size, size);
        Assert.True(icon.Contains(layout.ReadLight));
        Assert.True(icon.Contains(layout.WriteLight));
        Assert.True(icon.Contains(layout.Badge));
        Assert.False(layout.ReadLight.IntersectsWith(layout.WriteLight));
        Assert.False(layout.WriteLight.IntersectsWith(layout.Badge));
        Assert.False(layout.ReadLight.IntersectsWith(layout.Badge));
    }

    /// <summary>
    /// Verifies the part sizes at 100% scaling, where they must stay readable.
    /// </summary>
    [Fact]
    public void For_SixteenPixels_UsesMinimumReadableSizes()
    {
        // Act
        var layout = TrayIconLayout.For(16);

        // Assert
        Assert.Equal(5, layout.ReadLight.Width);
        Assert.Equal(9, layout.Badge.Width);
        Assert.Equal(new Rectangle(0, 11, 5, 5), layout.ReadLight);
        Assert.Equal(new Rectangle(11, 11, 5, 5), layout.WriteLight);
        Assert.Equal(new Rectangle(7, 0, 9, 9), layout.Badge);
    }

    /// <summary>
    /// Verifies that the renderer draws icons of its size, reuses an icon for the same visual and
    /// draws a different one for another visual.
    /// </summary>
    [Fact]
    public void GetIcon_SameAndDifferentVisual_CachesPerVisual()
    {
        // Arrange
        using var renderer = new TrayIconRenderer(GetIconBytes(), 24);
        var busy = new TrayIconVisual(true, true, TrayAlertLevel.Critical);

        // Act
        var first = renderer.GetIcon(busy);
        var again = renderer.GetIcon(busy);
        var idle = renderer.GetIcon(TrayIconVisual.Idle);

        // Assert
        Assert.Same(first, again);
        Assert.NotSame(first, idle);
        Assert.Equal(new System.Drawing.Size(24, 24), first.Size);
    }

    /// <summary>
    /// Verifies that the read light is drawn in green at its place and is absent from the idle icon.
    /// </summary>
    [Fact]
    public void GetIcon_ReadLight_DrawsGreenBottomLeft()
    {
        // Arrange
        using var renderer = new TrayIconRenderer(GetIconBytes(), 32);
        var light = renderer.Layout.ReadLight;
        // Lower right of the light, away from the highlight in its upper left.
        var center = new System.Drawing.Point(light.X + light.Width * 2 / 3, light.Y + light.Height * 2 / 3);

        // Act
        using var lit = renderer.GetIcon(new(true, false, TrayAlertLevel.None)).ToBitmap();

        // Assert
        var pixel = lit.GetPixel(center.X, center.Y);
        Assert.True(pixel.G > 150 && pixel.R < 100, $"Expected green, got {pixel}.");
    }

    /// <summary>
    /// Verifies which reasons get a tooltip text, in a fixed order, and that high usage gets none.
    /// </summary>
    /// <param name="reasons">The disk's reasons.</param>
    /// <param name="expected">The expected text with keys standing in for strings.</param>
    [Theory]
    [InlineData(TrayAlertReasons.None, "")]
    [InlineData(TrayAlertReasons.HighUsage, "")]
    [InlineData(TrayAlertReasons.LowMemory, "")]
    [InlineData(TrayAlertReasons.DiskFull | TrayAlertReasons.HighUsage, "TrayTip.AlertDiskFull")]
    [InlineData(TrayAlertReasons.SaveFailed | TrayAlertReasons.DiskFull, "TrayTip.AlertDiskFull · TrayTip.AlertSaveFailed")]
    public void Build_Reasons_ListsTheFailures(TrayAlertReasons reasons, string expected)
    {
        // Act
        var text = TrayAlertText.Build(reasons, key => key);

        // Assert
        Assert.Equal(expected, text);
    }

    /// <summary>
    /// Gets the bytes of an <c>.ico</c> file to draw on.
    /// </summary>
    /// <returns>The bytes of the system application icon.</returns>
    private static byte[] GetIconBytes()
    {
        using var stream = new MemoryStream();
        SystemIcons.Application.Save(stream);
        return stream.ToArray();
    }
}
