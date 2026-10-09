namespace ManagedDrive.App.Services;

/// <summary>
/// Decides how long the startup splash window stays up at least. Pure, so it
/// can be unit tested without a WPF dispatcher.
/// </summary>
internal static class SplashPolicy
{
    /// <summary>
    /// How long the splash stays up at least, so it does not flash by when there is nothing (or
    /// little) to load.
    /// </summary>
    public static readonly TimeSpan MinimumDisplay = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// How much longer the splash has to stay up to reach <see cref="MinimumDisplay"/>.
    /// </summary>
    /// <param name="elapsed">How long the splash has been up.</param>
    /// <returns>The remaining time, or <see cref="TimeSpan.Zero"/> if the minimum has passed.</returns>
    public static TimeSpan RemainingDisplay(TimeSpan elapsed)
    {
        var remaining = MinimumDisplay - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
