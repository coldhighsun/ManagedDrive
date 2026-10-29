namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Constants and helpers for the Zstd compression level used for saved images, snapshots and
/// <c>.mdr</c> exports. A level is a plain number: <see cref="None"/> (0) disables compression,
/// <see cref="Min"/> to <see cref="Max"/> are Zstd levels.
/// </summary>
public static class ZstdLevels
{
    /// <summary>
    /// Level meaning "do not compress".
    /// </summary>
    public const int None = 0;

    /// <summary>
    /// Lowest real Zstd level (fastest).
    /// </summary>
    public const int Min = 1;

    /// <summary>
    /// Highest Zstd level (smallest output).
    /// </summary>
    public const int Max = 22;

    /// <summary>
    /// Level used when nothing else is configured.
    /// </summary>
    public const int Default = 1;

    /// <summary>
    /// Highest level of the zip/7z archive export scale (Deflate/LZMA), which is not Zstd.
    /// </summary>
    public const int ArchiveMax = 9;

    /// <summary>
    /// Returns whether <paramref name="level"/> is <see cref="None"/> or within
    /// <see cref="Min"/> to <see cref="Max"/>.
    /// </summary>
    /// <param name="level">The level to check.</param>
    /// <returns><c>true</c> when the level is usable.</returns>
    public static bool IsValid(int level) => level is >= None and <= Max;

    /// <summary>
    /// Highest level that can be chosen for an export: <see cref="ArchiveMax"/> for a zip/7z
    /// archive, <see cref="Max"/> for a <c>.mdr</c> image.
    /// </summary>
    /// <param name="isArchive">Whether the export is a zip/7z archive.</param>
    /// <returns>The highest selectable level.</returns>
    public static int MaxFor(bool isArchive) => isArchive ? ArchiveMax : Max;

    /// <summary>
    /// Throws when <paramref name="level"/> is not a usable level, so a bad number is rejected where
    /// it enters the image/snapshot writers instead of being truncated into the header byte.
    /// </summary>
    /// <param name="level">The level to check.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 0-22.</exception>
    public static void ThrowIfInvalid(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, None);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, Max);
    }

    /// <summary>
    /// Converts a level saved by versions that used the preset enum (None 0, Fastest 1, Optimal 2,
    /// SmallestSize 3) and an optional custom Zstd level into the numeric level.
    /// </summary>
    /// <param name="legacyPreset">The old enum value.</param>
    /// <param name="legacyCustomLevel">The old custom Zstd level, which wins when set.</param>
    /// <returns>
    /// The equivalent numeric level; <see cref="Default"/> when the custom level is out of range.
    /// </returns>
    public static int FromLegacy(int legacyPreset, int? legacyCustomLevel)
    {
        if (legacyPreset == 0)
        {
            return None;
        }

        if (legacyCustomLevel is { } custom)
        {
            return custom is >= Min and <= Max ? custom : Default;
        }

        return legacyPreset switch
        {
            1 => 1,
            3 => 19,
            _ => 3,
        };
    }
}
