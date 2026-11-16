namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// The state of a disk's high-usage warning after one usage reading.
/// </summary>
/// <param name="IsHigh">Whether the disk is at or above its warning threshold right now.</param>
/// <param name="IsArmed">Whether the next rise above the threshold may raise the one-shot warning (balloon).</param>
/// <param name="RaiseWarning">Whether this reading raises the one-shot warning.</param>
public readonly record struct HighUsageState(bool IsHigh, bool IsArmed, bool RaiseWarning);

/// <summary>
/// Pure rules for a disk's high-usage warning. The warning state (card colour, tray blink, status bar) follows the
/// usage exactly and clears as soon as usage is back under the threshold. Only the one-shot warning that shows a
/// balloon is rate-limited: after it was raised it is armed again only once usage has fallen
/// <see cref="ResetGap"/> points below the threshold, so usage hovering around the threshold does not repeat it.
/// </summary>
public static class HighUsageRules
{
    /// <summary>
    /// How many percentage points below the threshold usage must fall to re-arm the one-shot warning.
    /// </summary>
    public const double ResetGap = 5.0;

    /// <summary>
    /// Computes the warning state for a new usage reading.
    /// </summary>
    /// <param name="usedPercent">The used percentage.</param>
    /// <param name="threshold">The warning threshold in percent, or <c>null</c> when the disk has no warning.</param>
    /// <param name="wasArmed">Whether the one-shot warning was armed before this reading.</param>
    /// <returns>The new state.</returns>
    public static HighUsageState Evaluate(double usedPercent, double? threshold, bool wasArmed)
    {
        if (threshold is not { } limit)
        {
            return new(false, true, false);
        }

        var isHigh = usedPercent >= limit;
        var armed = wasArmed || usedPercent < Math.Max(1.0, limit - ResetGap);
        var raise = isHigh && armed;

        return new(isHigh, armed && !raise, raise);
    }
}
