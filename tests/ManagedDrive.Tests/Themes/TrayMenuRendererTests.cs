using System.Drawing;
using ManagedDrive.App.Themes;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests the geometry <see cref="TrayMenuRenderer"/> uses to line up an item's text and submenu arrow.
/// </summary>
public sealed class TrayMenuRendererTests
{
    /// <summary>
    /// Verifies that text WinForms laid out at the top of a taller item moves to the vertical middle,
    /// keeping its horizontal position and size.
    /// </summary>
    [Fact]
    public void CenterVertically_ItemTallerThanText_CentersRectangle()
    {
        // Arrange
        var text = new Rectangle(8, 1, 71, 18);

        // Act
        var centered = TrayMenuRenderer.CenterVertically(text, itemHeight: 28);

        // Assert
        Assert.Equal(new Rectangle(8, 5, 71, 18), centered);
    }

    /// <summary>
    /// Verifies that text which already fills the whole item height stays at the top.
    /// </summary>
    [Fact]
    public void CenterVertically_TextFillsItem_StaysAtTop()
    {
        // Arrange
        var text = new Rectangle(8, 0, 71, 22);

        // Act
        var centered = TrayMenuRenderer.CenterVertically(text, itemHeight: 22);

        // Assert
        Assert.Equal(text, centered);
    }

    /// <summary>
    /// Verifies that the chevron is centered in the arrow rectangle, so it lines up with the text
    /// centered in the same item.
    /// </summary>
    [Fact]
    public void GetChevron_DefaultScale_IsCenteredInArrowBounds()
    {
        // Arrange
        var arrow = new Rectangle(88, 4, 10, 20);

        // Act
        var points = TrayMenuRenderer.GetChevron(arrow, scale: 1f);

        // Assert
        Assert.Equal(3, points.Length);
        Assert.Equal(14f, points[1].Y);
        Assert.Equal(points[1].Y - points[0].Y, points[2].Y - points[1].Y);
        Assert.True(points[1].X > points[0].X);
        Assert.Equal(points[0].X, points[2].X);
    }

    /// <summary>
    /// Verifies that the chevron grows with the DPI scale and stays centered on the same point.
    /// </summary>
    [Fact]
    public void GetChevron_DoubleScale_DoublesSizeAroundSameCenter()
    {
        // Arrange
        var arrow = new Rectangle(88, 4, 10, 20);

        // Act
        var normal = TrayMenuRenderer.GetChevron(arrow, scale: 1f);
        var doubled = TrayMenuRenderer.GetChevron(arrow, scale: 2f);

        // Assert
        Assert.Equal(14f, doubled[1].Y);
        Assert.Equal((normal[2].Y - normal[0].Y) * 2f, doubled[2].Y - doubled[0].Y);
    }
}
