using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="SplashPolicy"/>, which decides how
/// long the startup splash stays up at least.
/// </summary>
public sealed class SplashPolicyTests
{
    /// <summary>
    /// A splash that has just appeared has to stay for the whole minimum time.
    /// </summary>
    [Fact]
    public void RemainingDisplay_NothingElapsed_ReturnsMinimumDisplay()
    {
        Assert.Equal(SplashPolicy.MinimumDisplay, SplashPolicy.RemainingDisplay(TimeSpan.Zero));
    }

    /// <summary>
    /// Part of the minimum time already spent is deducted.
    /// </summary>
    [Fact]
    public void RemainingDisplay_PartiallyElapsed_ReturnsDifference()
    {
        var elapsed = TimeSpan.FromMilliseconds(200);

        Assert.Equal(SplashPolicy.MinimumDisplay - elapsed, SplashPolicy.RemainingDisplay(elapsed));
    }

    /// <summary>
    /// Once the minimum has passed (or exactly been reached) there is nothing left to wait for.
    /// </summary>
    [Theory]
    [InlineData(600)]
    [InlineData(5000)]
    public void RemainingDisplay_MinimumReached_ReturnsZero(int elapsedMilliseconds)
    {
        Assert.Equal(TimeSpan.Zero, SplashPolicy.RemainingDisplay(TimeSpan.FromMilliseconds(elapsedMilliseconds)));
    }
}
