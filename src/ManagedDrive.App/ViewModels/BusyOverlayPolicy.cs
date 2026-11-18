namespace ManagedDrive.App.ViewModels;

/// <summary>
/// Decides when the busy overlay becomes visible and how long it stays up at least, so an operation
/// that finishes quickly does not make the overlay flash by. Pure, so it can be unit tested without
/// a WPF dispatcher.
/// </summary>
internal static class BusyOverlayPolicy
{
    /// <summary>
    /// How long an operation has to run before the overlay is shown. An operation that finishes
    /// within this time never shows it at all.
    /// </summary>
    public static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How long the overlay stays up at least once it is shown, so an operation that finishes just
    /// after the delay does not make it appear and vanish at once.
    /// </summary>
    public static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How much longer the overlay has to stay up to reach <paramref name="minimumVisible"/>.
    /// </summary>
    /// <param name="shownFor">How long the overlay has been visible.</param>
    /// <param name="minimumVisible">The minimum time it has to stay up.</param>
    /// <returns>The remaining time, or <see cref="TimeSpan.Zero"/> if the minimum has passed.</returns>
    public static TimeSpan RemainingVisible(TimeSpan shownFor, TimeSpan minimumVisible)
    {
        var remaining = minimumVisible - shownFor;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
