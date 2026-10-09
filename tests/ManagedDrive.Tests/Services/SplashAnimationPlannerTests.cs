using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="SplashAnimationPlanner"/>, which picks the random parameters of the splash's
/// background animation.
/// </summary>
public sealed class SplashAnimationPlannerTests
{
    /// <summary>
    /// Width of the area the circles drift in, as the splash's background canvas.
    /// </summary>
    private const double Width = 536;

    /// <summary>
    /// Height of the area the circles drift in, as the splash's background canvas.
    /// </summary>
    private const double Height = 336;

    /// <summary>
    /// The number of circles and their look stay within the documented ranges.
    /// </summary>
    [Fact]
    public void CreateBlobs_ManySeeds_StaysWithinRanges()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var blobs = SplashAnimationPlanner.CreateBlobs(new Random(seed), Width, Height);

            Assert.InRange(blobs.Count, SplashAnimationPlanner.MinBlobs, SplashAnimationPlanner.MaxBlobs);
            foreach (var blob in blobs)
            {
                Assert.InRange(blob.Diameter, SplashAnimationPlanner.MinDiameter, SplashAnimationPlanner.MaxDiameter);
                Assert.InRange(blob.Opacity, SplashAnimationPlanner.MinOpacity, SplashAnimationPlanner.MaxOpacity);
                Assert.InRange(blob.DurationX, SplashAnimationPlanner.MinDriftDuration, SplashAnimationPlanner.MaxDriftDuration);
                Assert.InRange(blob.DurationY, SplashAnimationPlanner.MinDriftDuration, SplashAnimationPlanner.MaxDriftDuration);
            }
        }
    }

    /// <summary>
    /// Every circle's start and end centre lies within half a radius of the area, so it overlaps
    /// the window.
    /// </summary>
    [Fact]
    public void CreateBlobs_ManySeeds_KeepsCentresNearTheArea()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            foreach (var blob in SplashAnimationPlanner.CreateBlobs(new Random(seed), Width, Height))
            {
                var radius = blob.Diameter / 2;
                foreach (var (x, y) in new[] { (blob.StartX, blob.StartY), (blob.EndX, blob.EndY) })
                {
                    Assert.InRange(x + radius, -radius / 2, Width + radius / 2);
                    Assert.InRange(y + radius, -radius / 2, Height + radius / 2);
                }
            }
        }
    }

    /// <summary>
    /// The same seed gives the same circles, and different seeds give different ones.
    /// </summary>
    [Fact]
    public void CreateBlobs_SeedsDiffer_ProducesDifferentLayouts()
    {
        var first = SplashAnimationPlanner.CreateBlobs(new Random(1), Width, Height);
        var again = SplashAnimationPlanner.CreateBlobs(new Random(1), Width, Height);
        var other = SplashAnimationPlanner.CreateBlobs(new Random(2), Width, Height);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    /// <summary>
    /// The logo's scale and period stay within the documented ranges.
    /// </summary>
    [Fact]
    public void CreateLogoPulse_ManySeeds_StaysWithinRanges()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var pulse = SplashAnimationPlanner.CreateLogoPulse(new Random(seed));

            Assert.InRange(pulse.Scale, SplashAnimationPlanner.MinLogoScale, SplashAnimationPlanner.MaxLogoScale);
            Assert.InRange(pulse.Period, SplashAnimationPlanner.MinPulsePeriod, SplashAnimationPlanner.MaxPulsePeriod);
        }
    }
}
