namespace ManagedDrive.App.Services;

/// <summary>
/// One drifting background circle of the startup splash: its look and the two points it moves
/// between. Coordinates are the circle's top-left corner on the background canvas.
/// </summary>
/// <param name="Diameter">The circle's diameter.</param>
/// <param name="Opacity">The circle's opacity.</param>
/// <param name="StartX">The left coordinate it starts at.</param>
/// <param name="StartY">The top coordinate it starts at.</param>
/// <param name="EndX">The left coordinate it drifts to.</param>
/// <param name="EndY">The top coordinate it drifts to.</param>
/// <param name="DurationX">How long one way takes horizontally.</param>
/// <param name="DurationY">How long one way takes vertically.</param>
internal sealed record SplashBlob(
    double Diameter,
    double Opacity,
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    TimeSpan DurationX,
    TimeSpan DurationY);

/// <summary>
/// The breathing animation of the splash logo.
/// </summary>
/// <param name="Scale">The scale factor the logo grows to (above 1).</param>
/// <param name="Period">How long one way (growing or shrinking) takes.</param>
internal sealed record SplashLogoPulse(double Scale, TimeSpan Period);

/// <summary>
/// Picks random parameters for the splash's background animation, so every start looks a little
/// different. Pure, with the random source injected, so it can be unit tested.
/// </summary>
internal static class SplashAnimationPlanner
{
    /// <summary>
    /// The fewest circles drawn.
    /// </summary>
    public const int MinBlobs = 3;

    /// <summary>
    /// The most circles drawn.
    /// </summary>
    public const int MaxBlobs = 6;

    /// <summary>
    /// The smallest circle diameter.
    /// </summary>
    public const double MinDiameter = 80;

    /// <summary>
    /// The largest circle diameter.
    /// </summary>
    public const double MaxDiameter = 260;

    /// <summary>
    /// The lowest circle opacity.
    /// </summary>
    public const double MinOpacity = 0.05;

    /// <summary>
    /// The highest circle opacity.
    /// </summary>
    public const double MaxOpacity = 0.12;

    /// <summary>
    /// The shortest one-way drift time of a circle.
    /// </summary>
    public static readonly TimeSpan MinDriftDuration = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// The longest one-way drift time of a circle.
    /// </summary>
    public static readonly TimeSpan MaxDriftDuration = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The smallest scale the logo breathes up to.
    /// </summary>
    public const double MinLogoScale = 1.03;

    /// <summary>
    /// The largest scale the logo breathes up to.
    /// </summary>
    public const double MaxLogoScale = 1.12;

    /// <summary>
    /// The shortest one-way breathing time of the logo.
    /// </summary>
    public static readonly TimeSpan MinPulsePeriod = TimeSpan.FromSeconds(1.0);

    /// <summary>
    /// The longest one-way breathing time of the logo.
    /// </summary>
    public static readonly TimeSpan MaxPulsePeriod = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Picks the background circles. A circle's centre lies anywhere from half its radius outside
    /// the area to half its radius beyond the opposite edge, so each overlaps the window.
    /// </summary>
    /// <param name="random">The random source.</param>
    /// <param name="width">The width of the area the circles drift in.</param>
    /// <param name="height">The height of the area the circles drift in.</param>
    /// <returns>Between <see cref="MinBlobs"/> and <see cref="MaxBlobs"/> circles.</returns>
    public static IReadOnlyList<SplashBlob> CreateBlobs(Random random, double width, double height)
    {
        var count = random.Next(MinBlobs, MaxBlobs + 1);
        var blobs = new List<SplashBlob>(count);
        for (var i = 0; i < count; i++)
        {
            var diameter = Between(random, MinDiameter, MaxDiameter);
            var radius = diameter / 2;
            blobs.Add(new SplashBlob(
                diameter,
                Between(random, MinOpacity, MaxOpacity),
                RandomCorner(random, width, radius),
                RandomCorner(random, height, radius),
                RandomCorner(random, width, radius),
                RandomCorner(random, height, radius),
                BetweenSpans(random, MinDriftDuration, MaxDriftDuration),
                BetweenSpans(random, MinDriftDuration, MaxDriftDuration)));
        }

        return blobs;
    }

    /// <summary>
    /// Picks the logo's breathing animation.
    /// </summary>
    /// <param name="random">The random source.</param>
    /// <returns>The scale and one-way period.</returns>
    public static SplashLogoPulse CreateLogoPulse(Random random) => new(
        Between(random, MinLogoScale, MaxLogoScale),
        BetweenSpans(random, MinPulsePeriod, MaxPulsePeriod));

    /// <summary>
    /// Picks a top-left coordinate whose circle centre lies within half a radius of the area.
    /// </summary>
    /// <param name="random">The random source.</param>
    /// <param name="extent">The size of the area along this axis.</param>
    /// <param name="radius">The circle's radius.</param>
    /// <returns>The top-left coordinate.</returns>
    private static double RandomCorner(Random random, double extent, double radius)
        => Between(random, -radius / 2, extent + radius / 2) - radius;

    /// <summary>
    /// Picks a number in the given range.
    /// </summary>
    /// <param name="random">The random source.</param>
    /// <param name="min">The lower bound.</param>
    /// <param name="max">The upper bound.</param>
    /// <returns>A value from <paramref name="min"/> up to <paramref name="max"/>.</returns>
    private static double Between(Random random, double min, double max)
        => min + random.NextDouble() * (max - min);

    /// <summary>
    /// Picks a time span in the given range.
    /// </summary>
    /// <param name="random">The random source.</param>
    /// <param name="min">The lower bound.</param>
    /// <param name="max">The upper bound.</param>
    /// <returns>A span from <paramref name="min"/> up to <paramref name="max"/>.</returns>
    private static TimeSpan BetweenSpans(Random random, TimeSpan min, TimeSpan max)
        => TimeSpan.FromSeconds(Between(random, min.TotalSeconds, max.TotalSeconds));
}
