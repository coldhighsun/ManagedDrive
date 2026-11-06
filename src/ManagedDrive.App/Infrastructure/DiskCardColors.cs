namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// How full a disk looks on its card; the usage bar picks its colour from this.
/// </summary>
public enum UsageLevel
{
    /// <summary>Below the user's high-usage warning.</summary>
    Normal,

    /// <summary>At or above the high-usage warning threshold.</summary>
    High,

    /// <summary>Nearly full.</summary>
    Critical,
}

/// <summary>
/// Pure rules for the colours of a disk card: the usage level and which of the accent colours (<c>AppAccent1</c>-<c>AppAccent6</c>) a drive letter gets.
/// </summary>
public static class DiskCardColors
{
    /// <summary>The number of accent colours in the palette.</summary>
    public const int AccentCount = 6;

    /// <summary>The used percentage from which a disk counts as <see cref="UsageLevel.Critical"/>.</summary>
    public const double CriticalPercent = 95.0;

    /// <summary>
    /// Classifies a disk: critical from <see cref="CriticalPercent"/>, high when the high-usage warning is active.
    /// </summary>
    public static UsageLevel Classify(double usedPercent, bool isHighUsage) =>
        usedPercent >= CriticalPercent ? UsageLevel.Critical
        : isHighUsage ? UsageLevel.High
        : UsageLevel.Normal;

    /// <summary>
    /// Gives a mount point (<c>R:</c>, <c>R:\</c> or a path) a stable accent number from 1 to <see cref="AccentCount"/>, so each drive letter keeps its colour; anything without a letter gets 1.
    /// </summary>
    public static int AccentIndexFor(string? mountPoint)
    {
        if (string.IsNullOrEmpty(mountPoint) || !char.IsAsciiLetter(mountPoint[0]))
        {
            return 1;
        }

        return (char.ToUpperInvariant(mountPoint[0]) - 'A') % AccentCount + 1;
    }
}
