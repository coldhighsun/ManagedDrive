namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// Converts between raw byte counts and the MB/GB (value, unit) pairs used by the
/// create-disk dialog's capacity and snapshot-size inputs. Extracted so the conversion
/// logic lives in one place and can be unit-tested.
/// </summary>
public static class ByteUnitConverter
{
    private const ulong BytesPerMb = 1024UL * 1024;
    private const ulong BytesPerGb = 1024UL * 1024 * 1024;

    /// <summary>
    /// Splits a byte count into a (value, isGb) pair, preferring GB when the value is a whole
    /// number of gigabytes and is at least 1 GB; otherwise falling back to whole megabytes.
    /// </summary>
    /// <param name="bytes">The byte count to split.</param>
    /// <returns>
    /// A tuple of the display value and whether the unit is GB (<c>true</c>) or MB (<c>false</c>).
    /// </returns>
    public static (int Value, bool IsGb) SplitToUnit(ulong bytes)
    {
        var gb = bytes / BytesPerGb;
        if (gb > 0 && bytes % BytesPerGb == 0)
        {
            return ((int)gb, true);
        }

        return ((int)(bytes / BytesPerMb), false);
    }

    /// <summary>
    /// Converts a (value, isGb) pair back into a byte count.
    /// </summary>
    /// <param name="value">The display value.</param>
    /// <param name="isGb">Whether the value is in GB (<c>true</c>) or MB (<c>false</c>).</param>
    /// <returns>
    /// The equivalent byte count.
    /// </returns>
    public static ulong ToBytes(int value, bool isGb) =>
        isGb ? (ulong)value * BytesPerGb : (ulong)value * BytesPerMb;

    /// <summary>
    /// Returns the maximum display value that fits within <paramref name="maxBytes"/> for the
    /// given unit, clamped to at least 1 and at most <see cref="int.MaxValue"/>.
    /// </summary>
    /// <param name="maxBytes">The maximum available capacity in bytes.</param>
    /// <param name="isGb">Whether the unit is GB (<c>true</c>) or MB (<c>false</c>).</param>
    /// <returns>
    /// The clamped maximum display value.
    /// </returns>
    public static int MaxValueForUnit(ulong maxBytes, bool isGb)
    {
        var divisor = isGb ? BytesPerGb : BytesPerMb;
        return (int)Math.Max(1, Math.Min(maxBytes / divisor, int.MaxValue));
    }
}
