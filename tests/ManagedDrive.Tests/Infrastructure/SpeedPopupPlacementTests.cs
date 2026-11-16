using System.Windows.Controls.Primitives;
using ManagedDrive.App.Infrastructure;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="SpeedPopupPlacement"/>: on which side of its row the speed popup opens.
/// </summary>
public sealed class SpeedPopupPlacementTests
{
    /// <summary>
    /// Verifies that the popup opens below when it fits there, even when there is more room above.
    /// </summary>
    [Fact]
    public void Choose_FitsBelow_ReturnsBottom()
    {
        var placement = SpeedPopupPlacement.Choose(spaceBelow: 300, spaceAbove: 500, popupHeight: 230);

        Assert.Equal(PlacementMode.Bottom, placement);
    }

    /// <summary>
    /// Verifies that the popup flips above when it does not fit below but fits above.
    /// </summary>
    [Fact]
    public void Choose_DoesNotFitBelowButAbove_ReturnsTop()
    {
        var placement = SpeedPopupPlacement.Choose(spaceBelow: 100, spaceAbove: 300, popupHeight: 230);

        Assert.Equal(PlacementMode.Top, placement);
    }

    /// <summary>
    /// Verifies that the side with more room wins when the popup fits on neither.
    /// </summary>
    [Theory]
    [InlineData(120, 80, PlacementMode.Bottom)]
    [InlineData(80, 120, PlacementMode.Top)]
    public void Choose_FitsNowhere_ReturnsRoomierSide(double below, double above, PlacementMode expected)
    {
        var placement = SpeedPopupPlacement.Choose(below, above, popupHeight: 230);

        Assert.Equal(expected, placement);
    }
}
