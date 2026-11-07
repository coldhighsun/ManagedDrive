namespace ManagedDrive.App.Services;

/// <summary>
/// Pure helpers for the scrolling feature list of the startup splash.
/// </summary>
internal static class SplashTicker
{
    /// <summary>
    /// How fast the feature list scrolls, in device-independent pixels per second.
    /// </summary>
    public const double PixelsPerSecond = 28;

    /// <summary>
    /// The longest a full scroll is allowed to take, so a degenerate speed can't freeze it.
    /// </summary>
    public static readonly TimeSpan MaxScrollDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The fraction of the viewport, at its top and again at its bottom, over which the list fades
    /// out (the splash builds its opacity mask from this); the first pass starts just below the
    /// top fade.
    /// </summary>
    public const double EdgeFadeFraction = 0.2;

    /// <summary>
    /// Where the list's top edge starts on the first pass: at the end of the top fade, so the
    /// first line is fully visible at once instead of the area staying empty until the list has
    /// scrolled in from the bottom.
    /// </summary>
    /// <param name="viewportHeight">The height of the visible area.</param>
    /// <returns>The offset from the viewport's top; never negative.</returns>
    public static double InitialOffset(double viewportHeight) => Math.Max(viewportHeight, 0) * EdgeFadeFraction;

    /// <summary>
    /// How long one full pass takes: the list enters at the bottom of the viewport and leaves at
    /// the top, so it travels its own height plus the viewport's.
    /// </summary>
    /// <param name="contentHeight">The height of the whole list.</param>
    /// <param name="viewportHeight">The height of the visible area.</param>
    /// <param name="pixelsPerSecond">The scroll speed.</param>
    /// <returns>
    /// The time of one pass, never less than a millisecond or more than <see cref="MaxScrollDuration"/>;
    /// <see cref="MaxScrollDuration"/> if the speed is not positive.
    /// </returns>
    public static TimeSpan ScrollDuration(double contentHeight, double viewportHeight, double pixelsPerSecond)
    {
        if (pixelsPerSecond <= 0)
        {
            return MaxScrollDuration;
        }

        var seconds = (Math.Max(contentHeight, 0) + Math.Max(viewportHeight, 0)) / pixelsPerSecond;
        var duration = TimeSpan.FromSeconds(seconds);
        if (duration > MaxScrollDuration)
        {
            return MaxScrollDuration;
        }

        return duration < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : duration;
    }

    /// <summary>
    /// Returns the items starting at <paramref name="start"/> and wrapping around to the front.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="items">The items in their normal order.</param>
    /// <param name="start">The index to start at; wrapped into range, negatives included.</param>
    /// <returns>All items once, in rotated order; empty if there are none.</returns>
    public static IReadOnlyList<T> RotateFrom<T>(IReadOnlyList<T> items, int start)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var offset = ((start % items.Count) + items.Count) % items.Count;
        var result = new List<T>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            result.Add(items[(offset + i) % items.Count]);
        }

        return result;
    }
}
