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
}