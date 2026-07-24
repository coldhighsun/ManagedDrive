using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ManagedDrive.Core.Snapshots;

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
    /// Deletes a single snapshot of <paramref name="mainImagePath"/> at <paramref name="snapshotPath"/>,
    /// then garbage-collects any blob that is no longer referenced by a remaining snapshot.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="snapshotPath"/> does not match the snapshot naming scheme.
    /// </exception>
    public static void DeleteSnapshot(string mainImagePath, string snapshotPath)
    {
        if (!IsSnapshotFileName(Path.GetFileName(snapshotPath)))
        {
            throw new ArgumentException("Path is not a snapshot file.", nameof(snapshotPath));
        }

        try
        {
            File.Delete(snapshotPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        GarbageCollectBlobs(mainImagePath);
    }

    /// <summary>
    /// Compares the snapshot at <paramref name="indexPath"/> against <paramref name="currentMap"/>
    /// (the disk's live contents), classifying every path as added, removed, or modified based on
    /// the snapshot's already-stored SHA-256 hashes — no blob content is read. Files present in
    /// both with matching content are counted in <see cref="SnapshotDiffResult.UnchangedFileCount"/>.
    /// </summary>
    public static SnapshotDiffResult DiffAgainstCurrent(string indexPath, FileNodeMap currentMap)
    {
        var snapshotEntries = SnapshotStore.ReadEntries(indexPath);
        var snapshotByPath = new Dictionary<string, SnapshotStore.SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshotEntries)
        {
            snapshotByPath[entry.Path] = entry;
        }

        var addedFiles = new List<string>();
        var addedDirectories = new List<string>();
        var modifiedFiles = new List<string>();
        var unchangedFileCount = 0;
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in currentMap.GetAllNodes())
        {
            var path = kvp.Key;
            var node = kvp.Value;
            seenPaths.Add(path);

            if (!snapshotByPath.TryGetValue(path, out var snapshotEntry))
            {
                if (node.IsDirectory)
                {
                    addedDirectories.Add(path);
                }
                else
                {
                    addedFiles.Add(path);
                }

                continue;
            }

            if (node.IsDirectory)
            {
                continue;
            }

            var currentIsEmpty = node.FileData is null || node.FileInfo.FileSize == 0;
            bool matches;
            if (currentIsEmpty)
            {
                matches = snapshotEntry.FileSize == 0;
            }
            else
            {
                var currentHash = ComputeHash(node);
                matches = snapshotEntry.Hash is not null && currentHash.AsSpan().SequenceEqual(snapshotEntry.Hash);
            }

            if (matches)
            {
                unchangedFileCount++;
            }
            else
            {
                modifiedFiles.Add(path);
            }
        }

        var removedFiles = new List<string>();
        var removedDirectories = new List<string>();
        foreach (var entry in snapshotEntries)
        {
            if (seenPaths.Contains(entry.Path))
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                removedDirectories.Add(entry.Path);
            }
            else
            {
                removedFiles.Add(entry.Path);
            }
        }

        return new(addedFiles, removedFiles, modifiedFiles, addedDirectories, removedDirectories, unchangedFileCount);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="fileName"/> matches the snapshot naming
    /// scheme produced by <see cref="BuildSnapshotPath"/> (i.e. it is a snapshot, not a
    /// plain main image file).
    /// </summary>
    public static bool IsSnapshotFileName(string fileName) => SnapshotPattern().IsMatch(fileName);

    /// <summary>
    /// Lists every node in the snapshot at <paramref name="indexPath"/> (path, directory flag,
    /// logical size) without reading any blob content — a lightweight "what's in this snapshot"
    /// listing for browsing prior to a full restore.
    /// </summary>
    public static IReadOnlyList<SnapshotContentEntry> ListSnapshotContents(string indexPath) =>
        SnapshotStore.ReadEntries(indexPath)
            .Select(e => new SnapshotContentEntry(e.Path, e.IsDirectory, e.FileSize))
            .ToList();

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
    public static FileNodeMap LoadSnapshot(string indexPath, out ulong capacityBytes, out string volumeLabel, byte[]? cek = null) =>
        SnapshotStore.Load(indexPath, BlobDirectoryFromSnapshotPath(indexPath), out capacityBytes, out volumeLabel, cek);

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
        ImageCompressionLevel level,
        byte[]? cek = null,
        IProgress<double>? progress = null)
    {
        var indexPath = BuildSnapshotPath(mainImagePath, timestampUtc);
        SnapshotStore.Write(nodeMap, capacityBytes, volumeLabel, indexPath, BlobDirectory(mainImagePath), level, cek, progress);
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

    private static byte[] ComputeHash(FileNode node)
    {
        var fileSize = (int)Math.Min(node.FileInfo.FileSize, (ulong)node.FileData!.Length);
        return SHA256.HashData(node.FileData.AsSpan(0, fileSize));
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

    /// <summary>
    /// One node listed by <see cref="ListSnapshotContents"/>: its path, whether it is a
    /// directory, and its logical size in bytes.
    /// </summary>
    public readonly record struct SnapshotContentEntry(string Path, bool IsDirectory, ulong SizeBytes);

    /// <summary>
    /// Result of comparing a snapshot against a disk's live contents, as computed by
    /// <see cref="DiffAgainstCurrent"/>.
    /// </summary>
    public readonly record struct SnapshotDiffResult(
        IReadOnlyList<string> AddedFiles,
        IReadOnlyList<string> RemovedFiles,
        IReadOnlyList<string> ModifiedFiles,
        IReadOnlyList<string> AddedDirectories,
        IReadOnlyList<string> RemovedDirectories,
        int UnchangedFileCount)
    {
        /// <summary>
        /// <c>true</c> when the snapshot differs from the current contents in any way (any
        /// added, removed, or modified file or directory); <c>false</c> when they are identical.
        /// </summary>
        public bool HasChanges =>
            AddedFiles.Count > 0 || RemovedFiles.Count > 0 || ModifiedFiles.Count > 0 ||
            AddedDirectories.Count > 0 || RemovedDirectories.Count > 0;
    }
}