using ManagedDrive.App.Infrastructure;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="DragPositionRules"/>: when the drop hint is hidden.
/// </summary>
public sealed class DragPositionRulesTests
{
    /// <summary>
    /// Verifies that a pointer inside the window, edges included, is not outside.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 50)]
    [InlineData(799.5, 599.5)]
    public void IsOutside_InsideWindow_ReturnsFalse(double x, double y)
    {
        var outside = DragPositionRules.IsOutside(x, y, width: 800, height: 600);

        Assert.False(outside);
    }

    /// <summary>
    /// Verifies that a pointer beyond any edge is outside.
    /// </summary>
    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(800, 10)]
    [InlineData(10, 600)]
    public void IsOutside_BeyondAnEdge_ReturnsTrue(double x, double y)
    {
        var outside = DragPositionRules.IsOutside(x, y, width: 800, height: 600);

        Assert.True(outside);
    }
}
