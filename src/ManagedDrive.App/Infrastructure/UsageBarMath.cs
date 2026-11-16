namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Pure maths of the animated usage bar on a disk card.
/// </summary>
public static class UsageBarMath
{
    /// <summary>
    /// Converts a used percentage to the horizontal scale of the bar's fill: 0 to 100 maps to 0 to 1, anything
    /// outside is clamped and a value that is not a number counts as 0.
    /// </summary>
    /// <param name="percent">The used percentage.</param>
    /// <returns>The scale from 0 to 1.</returns>
    public static double ToScale(double percent)
    {
        if (double.IsNaN(percent))
        {
            return 0;
        }

        return Math.Clamp(percent, 0, 100) / 100;
    }
}
