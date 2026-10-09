using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="SplashTicker"/>, the pure helpers behind the splash's scrolling feature list.
/// </summary>
public sealed class SplashTickerTests
{
    /// <summary>
    /// A pass covers the list's height plus the viewport's, at the given speed.
    /// </summary>
    [Fact]
    public void ScrollDuration_NormalInput_ReturnsDistanceOverSpeed()
    {
        var duration = SplashTicker.ScrollDuration(contentHeight: 200, viewportHeight: 80, pixelsPerSecond: 28);

        Assert.Equal(TimeSpan.FromSeconds(10), duration);
    }

    /// <summary>
    /// The first pass starts just below the top fade.
    /// </summary>
    [Theory]
    [InlineData(100, 20)]
    [InlineData(0, 0)]
    [InlineData(-50, 0)]
    public void InitialOffset_Viewport_IsEdgeFadeFraction(double viewportHeight, double expected)
    {
        Assert.Equal(expected, SplashTicker.InitialOffset(viewportHeight), precision: 6);
    }

    /// <summary>
    /// A speed that is not positive can't divide by zero or run forever.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ScrollDuration_NonPositiveSpeed_ReturnsMaximum(double speed)
    {
        Assert.Equal(SplashTicker.MaxScrollDuration, SplashTicker.ScrollDuration(100, 100, speed));
    }

    /// <summary>
    /// An enormous list is capped at the maximum.
    /// </summary>
    [Fact]
    public void ScrollDuration_HugeContent_IsCapped()
    {
        Assert.Equal(SplashTicker.MaxScrollDuration, SplashTicker.ScrollDuration(1e9, 100, 28));
    }

    /// <summary>
    /// An empty list and viewport still give a usable, non-zero duration.
    /// </summary>
    [Fact]
    public void ScrollDuration_NothingToScroll_ReturnsAtLeastOneMillisecond()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1), SplashTicker.ScrollDuration(0, 0, 28));
    }

    /// <summary>
    /// The rotation starts at the given item and wraps around.
    /// </summary>
    [Fact]
    public void RotateFrom_MiddleIndex_WrapsAround()
    {
        Assert.Equal([3, 4, 1, 2], SplashTicker.RotateFrom([1, 2, 3, 4], 2));
    }

    /// <summary>
    /// A start outside the list, even a negative one, is wrapped into range.
    /// </summary>
    [Theory]
    [InlineData(4, new[] { 1, 2, 3, 4 })]
    [InlineData(-1, new[] { 4, 1, 2, 3 })]
    [InlineData(9, new[] { 2, 3, 4, 1 })]
    public void RotateFrom_OutOfRangeStart_IsWrapped(int start, int[] expected)
    {
        Assert.Equal(expected, SplashTicker.RotateFrom([1, 2, 3, 4], start));
    }

    /// <summary>
    /// An empty list stays empty whatever the start.
    /// </summary>
    [Fact]
    public void RotateFrom_EmptyList_ReturnsEmpty()
    {
        Assert.Empty(SplashTicker.RotateFrom(Array.Empty<string>(), 3));
    }
}
