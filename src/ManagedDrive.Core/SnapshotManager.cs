using System.Text.RegularExpressions;

namespace ManagedDrive.Core;

/// <summary>
/// Builds, lists, and prunes timestamped snapshot copies of a disk's <c>.mdr</c> image,
/// stored alongside the main image file.
/// </summary>
public static partial class SnapshotManager
{
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Builds a unique snapshot file path in the same directory as <paramref name="mainImagePath"/>,
    /// named <c>{baseName}.{yyyyMMdd-HHmmss}.mdr</c>. Appends <c>-N</c> when a file with the
    /// same timestamp already exists (same-second collision).
    /// </summary>
    public static string BuildSnapshotPath(string mainImagePath, DateTimeOffset timestampUtc)
    {
        var directory = Path.GetDirectoryName(mainImagePath);
        var baseName = Path.GetFileNameWithoutExtension(mainImagePath);
        var timestamp = timestampUtc.ToString(TimestampFormat);

        var candidate = Path.Combine(directory ?? string.Empty, $"{baseName}.{timestamp}.mdr");
        var seq = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory ?? string.Empty, $"{baseName}.{timestamp}-{seq}.mdr");
            seq++;
        }

        return candidate;
    }

    /// <summary>
    /// Deletes every snapshot index file of <paramref name="mainImagePath"/> along with its
    /// entire blob directory. Used when the user deletes the main image itself, so no orphaned
    /// snapshots or blobs are left behind. Failures deleting an individual file are skipped.
    /// </summary>
    public static void DeleteAllSnapshots(string mainImagePath)
    {
        foreach (var snapshot in ListSnapshots(mainImagePath))
        {
            try
            {
                File.Delete(snapshot.Path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var blobDirectory = BlobDirectory(mainImagePath);
        try
        {
            if (Directory.Exists(blobDirectory))
            {
                Directory.Delete(blobDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="fileName"/> matches the snapshot naming
    /// scheme produced by <see cref="BuildSnapshotPath"/> (i.e. it is a snapshot, not a
    /// plain main image file).
    /// </summary>
    public static bool IsSnapshotFileName(string fileName) => SnapshotPattern().IsMatch(fileName);

    /// <summary>
    /// Lists all snapshot index files belonging to <paramref name="mainImagePath"/>, sorted
    /// oldest first. Snapshots whose timestamp can't be parsed from the filename fall back to
    /// the file's last-write time. <see cref="SnapshotInfo.SizeBytes"/> is the snapshot's total
    /// logical (pre-dedup, uncompressed) content size, read cheaply from the index header
    /// without touching any blob.
    /// </summary>
    public static List<SnapshotInfo> ListSnapshots(string mainImagePath)
    {
        var directory = Path.GetDirectoryName(mainImagePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var baseName = Path.GetFileNameWithoutExtension(mainImagePath);
        var snapshots = new List<SnapshotInfo>();

        foreach (var path in Directory.EnumerateFiles(directory, $"{baseName}.*.mdr"))
        {
            var fileName = Path.GetFileName(path);
            var match = SnapshotPattern().Match(fileName);
            if (!match.Success || !string.Equals(match.Groups["base"].Value, baseName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var timestamp = DateTimeOffset.TryParseExact(
                match.Groups["ts"].Value,
                TimestampFormat,
                null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

            var logicalSize = SnapshotStore.ReadSummary(path).LogicalSizeBytes;
            snapshots.Add(new(path, timestamp, logicalSize));
        }

        snapshots.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return snapshots;
    }

    /// <summary>
    /// Loads the snapshot index at <paramref name="indexPath"/>, resolving its content from
    /// the blob store shared by all snapshots of the same main image.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Thrown when the index is not a valid snapshot, its version is unsupported, or a
    /// referenced blob is missing or corrupt.
    /// </exception>
    public static FileNodeMap LoadSnapshot(string indexPath, out ulong capacityBytes, out string volumeLabel) =>
        SnapshotStore.Load(indexPath, BlobDirectoryFromSnapshotPath(indexPath), out capacityBytes, out volumeLabel);

    /// <summary>
    /// Deletes the oldest snapshots of <paramref name="mainImagePath"/> until both
    /// <paramref name="maxCount"/> and <paramref name="maxTotalBytes"/> are satisfied (or no
    /// snapshots remain), then garbage-collects any blob no longer referenced by a remaining
    /// snapshot. A no-op (including skipping GC) when both limits are <c>null</c>. Failures
    /// deleting an individual file are skipped rather than retried.
    /// </summary>
    public static void Prune(string mainImagePath, uint? maxCount, ulong? maxTotalBytes)
    {
        if (maxCount is null && maxTotalBytes is null)
        {
            return;
        }

        var snapshots = ListSnapshots(mainImagePath);
        var totalBytes = 0UL;
        foreach (var snapshot in snapshots)
        {
            totalBytes += (ulong)snapshot.SizeBytes;
        }

        var remainingCount = (uint)snapshots.Count;

        var index = 0;
        while (index < snapshots.Count &&
               ((maxCount is { } mc && remainingCount > mc) ||
                (maxTotalBytes is { } mb && totalBytes > mb)))
        {
            var snapshot = snapshots[index];
            try
            {
                File.Delete(snapshot.Path);
                totalBytes -= (ulong)snapshot.SizeBytes;
                remainingCount--;
            }
            catch (IOException)
            {
                // Best-effort pruning; leave this file for the next attempt.
            }
            catch (UnauthorizedAccessException)
            {
            }

            index++;
        }

        GarbageCollectBlobs(mainImagePath);
    }

    /// <summary>
    /// Writes <paramref name="nodeMap"/> as a new timestamped snapshot of <paramref name="mainImagePath"/>.
    /// File content is deduplicated across all snapshots of this image via a shared,
    /// content-addressed blob store.
    /// </summary>
    public static void WriteSnapshot(
        FileNodeMap nodeMap,
        ulong capacityBytes,
        string volumeLabel,
        string mainImagePath,
        DateTimeOffset timestampUtc,
        ImageCompressionLevel level)
    {
        var indexPath = BuildSnapshotPath(mainImagePath, timestampUtc);
        SnapshotStore.Write(nodeMap, capacityBytes, volumeLabel, indexPath, BlobDirectory(mainImagePath), level);
    }

    private static string BlobDirectory(string mainImagePath) => SnapshotStore.ComputeBlobDirectory(mainImagePath);

    /// <summary>
    /// Derives the shared blob directory from a snapshot index file's own path (rather than
    /// the main image path, which the caller may not have on hand), by extracting the main
    /// image's base name from the snapshot's timestamped filename.
    /// </summary>
    private static string BlobDirectoryFromSnapshotPath(string indexPath)
    {
        var directory = Path.GetDirectoryName(indexPath) ?? string.Empty;
        var match = SnapshotPattern().Match(Path.GetFileName(indexPath));
        var baseName = match.Success ? match.Groups["base"].Value : Path.GetFileNameWithoutExtension(indexPath);
        return Path.Combine(directory, baseName + ".snapblobs");
    }

    /// <summary>
    /// Deletes every blob in this image's blob directory that is not referenced by any
    /// remaining snapshot (mark-and-sweep; no persistent reference counts).
    /// </summary>
    private static void GarbageCollectBlobs(string mainImagePath)
    {
        var blobDirectory = BlobDirectory(mainImagePath);
        if (!Directory.Exists(blobDirectory))
        {
            return;
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in ListSnapshots(mainImagePath))
        {
            foreach (var hash in SnapshotStore.ReadSummary(snapshot.Path).ReferencedHashesHex)
            {
                referenced.Add(hash);
            }
        }

        foreach (var blobPath in Directory.EnumerateFiles(blobDirectory, "*.blob", SearchOption.AllDirectories))
        {
            var hashHex = Path.GetFileNameWithoutExtension(blobPath);
            if (referenced.Contains(hashHex))
            {
                continue;
            }

            try
            {
                File.Delete(blobPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [GeneratedRegex(@"^(?<base>.+)\.(?<ts>\d{8}-\d{6})(-\d+)?\.mdr$", RegexOptions.IgnoreCase)]
    private static partial Regex SnapshotPattern();

    /// <summary>
    /// Metadata for a single snapshot file.
    /// </summary>
    public readonly record struct SnapshotInfo(string Path, DateTimeOffset TimestampUtc, long SizeBytes);
}