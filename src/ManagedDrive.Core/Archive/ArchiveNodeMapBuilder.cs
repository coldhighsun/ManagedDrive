using SharpCompress.Archives;
using SharpCompress.Common;

namespace ManagedDrive.Core.Archive;

/// <summary>
/// Builds a <see cref="FileNodeMap"/> by extracting an archive (zip, 7z, rar, tar, and any other
/// format <c>SharpCompress</c> can read) into memory, so it can be mounted the same way an
/// existing <c>.mdr</c> image is loaded by <see cref="DiskImageSerializer"/>. Archive-sourced
/// disks are always read-only, since none of the supported formats support the random
/// read/write access a RAM disk needs to write changes back.
/// </summary>
public static class ArchiveNodeMapBuilder
{
    /// <summary>
    /// Extracts every entry in <paramref name="archivePath"/> into a new <see cref="FileNodeMap"/>,
    /// synthesizing any directory nodes an archive doesn't explicitly list.
    /// </summary>
    /// <param name="archivePath">Path to the archive file.</param>
    /// <param name="totalBytes">
    /// The total uncompressed byte size previously computed by <see cref="PeekArchive"/>, used to
    /// normalize <paramref name="progress"/> reports as a byte-weighted fraction. When
    /// <see langword="null"/> or non-positive, progress is not reported mid-extraction (an
    /// unreliable ad-hoc total would be misleading) — only a final 1.0 report is made.
    /// </param>
    /// <param name="progress">Optional progress reporter, updated with a fraction in [0, 1].</param>
    /// <returns>A populated node map, including the root directory entry.</returns>
    /// <exception cref="InvalidDataException">
    /// The file is not a format SharpCompress can read, or the archive is otherwise invalid.
    /// </exception>
    public static FileNodeMap BuildNodeMap(string archivePath, long? totalBytes = null, IProgress<double>? progress = null)
    {
        var nodeMap = new FileNodeMap();
        var now = (ulong)DateTimeOffset.UtcNow.ToFileTime();
        var reportProgress = totalBytes is > 0;
        var processedBytes = 0L;

        EnsureRoot(nodeMap, now);

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);

            void ProcessEntry(IEntry entry, Func<Stream> openEntryStream)
            {
                var path = NormalizeEntryPath(entry.Key);
                if (path is null)
                {
                    return;
                }

                var timestamp = entry.LastModifiedTime is { } lastModified
                    ? (ulong)lastModified.ToUniversalTime().ToFileTimeUtc()
                    : now;

                EnsureAncestorDirectories(nodeMap, path, timestamp);

                if (entry.IsDirectory)
                {
                    EnsureDirectory(nodeMap, path, timestamp);
                    return;
                }

                AddFile(nodeMap, path, openEntryStream, entry, timestamp);

                if (reportProgress)
                {
                    processedBytes += entry.Size;
                    progress?.Report(Math.Clamp((double)processedBytes / totalBytes!.Value, 0.0, 1.0));
                }
            }

            // Opening each entry individually via IArchiveEntry.OpenEntryStream (the random-access
            // Archive API used in the `else` branch below) re-decodes a solid block from its start
            // every time, since no decoder state carries over between entries — extracting N
            // entries out of a solid block costs roughly O(N) full block decompressions. 7z is
            // solid by default and always requires this random-access reopening internally even
            // when the caller asks for entries in order, so ExtractAllEntries() (a forward-only
            // IReader that walks the same folder decoder across entries, decoding each byte
            // exactly once) is required for 7z/solid archives to avoid minutes-long extraction on
            // a large archive. It's restricted to solid archives and 7z, so non-solid formats
            // (plain zip, tar, ...) keep using the simpler per-entry API, where random access is
            // already cheap.
            if (archive.Type == ArchiveType.SevenZip || archive.IsSolid)
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    ProcessEntry(reader.Entry, reader.OpenEntryStream);
                }
            }
            else
            {
                foreach (var entry in archive.Entries)
                {
                    ProcessEntry(entry, entry.OpenEntryStream);
                }
            }
        }
        // Cancellation (raised through progress) and the low-memory guard aren't signs of an
        // unreadable archive; let them through so callers report them as what they are.
        catch (Exception ex) when (ex is not (InvalidDataException or OperationCanceledException or InsufficientMemoryException))
        {
            throw new InvalidDataException($"Not a supported archive file: {archivePath}", ex);
        }

        progress?.Report(1.0);
        return nodeMap;
    }

    /// <summary>
    /// Opens <paramref name="archivePath"/> and sums the uncompressed size of every entry,
    /// without extracting any file content. Used to preview the capacity/label a disk would get
    /// before committing to a full extraction.
    /// </summary>
    /// <param name="archivePath">Path to the archive file.</param>
    /// <param name="totalBytes">The sum of every file entry's uncompressed size.</param>
    /// <param name="suggestedLabel">A volume label derived from the archive's file name.</param>
    /// <exception cref="InvalidDataException">
    /// The file is not a format SharpCompress can read, or the archive is otherwise invalid.
    /// </exception>
    public static void PeekArchive(string archivePath, out ulong totalBytes, out string suggestedLabel)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);

            var total = 0UL;
            foreach (var entry in archive.Entries)
            {
                if (!entry.IsDirectory)
                {
                    total += (ulong)RequireNonNegativeSize(entry);
                }
            }

            totalBytes = total;
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"Not a supported archive file: {archivePath}", ex);
        }

        suggestedLabel = Path.GetFileNameWithoutExtension(archivePath);
    }

    private static void AddFile(FileNodeMap nodeMap, string path, Func<Stream> openEntryStream, IEntry entry, ulong timestamp)
    {
        RequireNoTypeConflict(nodeMap, path, expectDirectory: false);

        var size = (ulong)RequireNonNegativeSize(entry);
        var allocationSize = FileNode.AlignToAllocationUnit(size);
        var data = FileContent.CreateZeroed(allocationSize);

        using (var entryStream = openEntryStream())
        {
            data.FillFromStream(entryStream, (long)size);
        }

        var node = new FileNode
        {
            FileData = data,
            FileSecurity = FileNode.DefaultSecurityDescriptorBytes,
            FileInfo =
            {
                FileAttributes = (uint)FileAttributes.Normal,
                FileSize = size,
                AllocationSize = allocationSize,
                CreationTime = timestamp,
                LastAccessTime = timestamp,
                LastWriteTime = timestamp,
                ChangeTime = timestamp,
                IndexNumber = FileNode.NewIndexNumber(),
            },
        };

        nodeMap.Add(path, node);
    }

    /// <summary>
    /// Returns <see cref="IEntry.Size"/>, rejecting a negative value up front. A negative size
    /// (from a corrupted or maliciously crafted archive header) would otherwise wrap to a huge
    /// <see cref="ulong"/> once cast, triggering a multi-exabyte allocation attempt instead of a
    /// clean "invalid archive" error.
    /// </summary>
    private static long RequireNonNegativeSize(IEntry entry)
    {
        if (entry.Size < 0)
        {
            throw new InvalidDataException($"Archive entry '{entry.Key}' has an invalid negative size ({entry.Size}).");
        }

        return entry.Size;
    }

    private static void EnsureAncestorDirectories(FileNodeMap nodeMap, string path, ulong timestamp)
    {
        var separatorIndex = path.IndexOf('\\', 1);
        while (separatorIndex > 0)
        {
            EnsureDirectory(nodeMap, path[..separatorIndex], timestamp);
            separatorIndex = path.IndexOf('\\', separatorIndex + 1);
        }
    }

    private static void EnsureDirectory(FileNodeMap nodeMap, string path, ulong timestamp)
    {
        if (RequireNoTypeConflict(nodeMap, path, expectDirectory: true) is not null)
        {
            // Already exists and is a directory (RequireNoTypeConflict would have thrown
            // otherwise) — nothing to do.
            return;
        }

        nodeMap.Add(path, NewDirectoryNode(timestamp));
    }

    /// <summary>
    /// Throws if a node already exists at <paramref name="path"/> whose <see cref="FileNode.IsDirectory"/>
    /// doesn't match <paramref name="expectDirectory"/> — an archive that uses the same path as both
    /// a file and a directory. Returns the existing node (or <see langword="null"/> if nothing is
    /// there yet) so callers that also need to know whether something already exists don't have to
    /// look it up a second time.
    /// </summary>
    private static FileNode? RequireNoTypeConflict(FileNodeMap nodeMap, string path, bool expectDirectory)
    {
        nodeMap.TryGet(path, out var existing);

        if (existing is { } node && node.IsDirectory != expectDirectory)
        {
            throw new InvalidDataException($"Archive entry '{path}' is used as both a file and a directory.");
        }

        return existing;
    }

    private static void EnsureRoot(FileNodeMap nodeMap, ulong timestamp)
    {
        if (nodeMap.TryGet("\\", out _))
        {
            return;
        }

        nodeMap.Add("\\", NewDirectoryNode(timestamp));
    }

    private static FileNode NewDirectoryNode(ulong timestamp)
    {
        return new()
        {
            FileSecurity = FileNode.DefaultSecurityDescriptorBytes,
            FileInfo =
            {
                FileAttributes = (uint)FileAttributes.Directory,
                CreationTime = timestamp,
                LastAccessTime = timestamp,
                LastWriteTime = timestamp,
                ChangeTime = timestamp,
                IndexNumber = FileNode.NewIndexNumber(),
            },
        };
    }

    /// <summary>
    /// Converts an archive entry key (<c>/</c>-separated, possibly with a trailing slash for
    /// directory entries) into an absolute WinFsp path (<c>\</c>-separated, rooted at <c>\</c>).
    /// </summary>
    /// <returns><c>null</c> if the entry key is empty (nothing to add).</returns>
    private static string? NormalizeEntryPath(string? entryKey)
    {
        if (string.IsNullOrEmpty(entryKey))
        {
            return null;
        }

        var normalized = entryKey.Replace('/', '\\').Trim('\\');
        return normalized.Length == 0 ? null : "\\" + normalized;
    }
}
