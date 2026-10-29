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

    /// <summary>
    /// Parses a typed capacity value. The upper limit is deliberately not applied so the caller can
    /// report a value that is too large instead of silently changing it.
    /// </summary>
    /// <param name="text">The text typed by the user.</param>
    /// <param name="fallback">The value returned when the text is not a whole number.</param>
    /// <returns>
    /// The parsed value, at least 1 and at most <see cref="int.MaxValue"/>, or
    /// <paramref name="fallback"/> when the text is empty or not numeric.
    /// </returns>
    public static int ParseCapacityInput(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (!long.TryParse(text.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return fallback;
        }

        return (int)Math.Clamp(parsed, 1, int.MaxValue);
    }

    /// <summary>
    /// Moves a capacity value up or down by a number of steps. The allowed values are 1 to
    /// <paramref name="normalMaximum"/>, plus the edited disk's own capacity when that is larger
    /// (nothing in between), so a step crossing the limit jumps straight to the other edge.
    /// </summary>
    /// <param name="current">The current value; it may exceed the limit when an over-limit value was typed.</param>
    /// <param name="delta">The signed step size.</param>
    /// <param name="normalMaximum">The largest value allowed for a new capacity (the available-memory limit).</param>
    /// <param name="existingValue">The edited disk's own capacity in the same unit, or <c>null</c>.</param>
    /// <returns>
    /// The stepped value: at least 1, and either at most <paramref name="normalMaximum"/> or exactly
    /// <paramref name="existingValue"/> when that exceeds the limit.
    /// </returns>
    public static int AddToCapacity(int current, int delta, int normalMaximum, ulong? existingValue)
    {
        var limit = Math.Max(1, normalMaximum);
        var target = (long)current + delta;
        if (target <= limit)
        {
            return (int)Math.Max(target, 1);
        }

        if (existingValue is not { } existing || existing <= (ulong)limit)
        {
            return limit;
        }

        // Going up from below the limit stops on it first; the next step reaches the existing value.
        if (delta > 0 && current < limit)
        {
            return limit;
        }

        return delta > 0 ? (int)Math.Min(existing, int.MaxValue) : limit;
    }
}
