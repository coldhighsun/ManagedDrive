using ManagedDrive.App.Infrastructure;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="UsageBarMath"/>: the scale of the usage bar's fill.
/// </summary>
public sealed class UsageBarMathTests
{
    /// <summary>
    /// Verifies that percentages from 0 to 100 map linearly to 0 to 1.
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(50.0, 0.5)]
    [InlineData(100.0, 1.0)]
    public void ToScale_InRange_MapsLinearly(double percent, double expected)
    {
        var scale = UsageBarMath.ToScale(percent);

        Assert.Equal(expected, scale, precision: 6);
    }

    /// <summary>
    /// Verifies that values outside 0 to 100 are clamped.
    /// </summary>
    [Theory]
    [InlineData(-5.0, 0.0)]
    [InlineData(250.0, 1.0)]
    public void ToScale_OutOfRange_IsClamped(double percent, double expected)
    {
        var scale = UsageBarMath.ToScale(percent);

        Assert.Equal(expected, scale, precision: 6);
    }

    /// <summary>
    /// Verifies that a value that is not a number gives an empty bar.
    /// </summary>
    [Fact]
    public void ToScale_NaN_ReturnsZero()
    {
        var scale = UsageBarMath.ToScale(double.NaN);

        Assert.Equal(0, scale);
    }
}
