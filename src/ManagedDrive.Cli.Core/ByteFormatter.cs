namespace ManagedDrive.Cli.Core;

/// <summary>
/// Formats byte counts as human-readable strings (e.g. "12.3 MB").
/// </summary>
public static class ByteFormatter
{
    /// <summary>
    /// Formats <paramref name="bytes"/> using the largest whole unit (GB/MB/KB/B) that keeps
    /// the value at or above 1.
    /// </summary>
    public static string Format(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        if (bytes >= 1024UL * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        if (bytes >= 1024UL)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        return $"{bytes} B";
    }

    /// <summary>
    /// Formats a byte-per-second rate using the same unit thresholds as <see cref="Format"/>,
    /// with an "/s" suffix (e.g. "1.2 MB/s"). Values below 1 B/s are reported as "0 B/s".
    /// </summary>
    public static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1.0)
        {
            return "0 B/s";
        }

        return $"{Format((ulong)Math.Round(bytesPerSecond))}/s";
    }
}