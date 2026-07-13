using SharpCompress.Archives;

namespace ManagedDrive.Core;

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
    /// <returns>A populated node map, including the root directory entry.</returns>
    /// <exception cref="InvalidDataException">
    /// The file is not a format SharpCompress can read, or the archive is otherwise invalid.
    /// </exception>
    public static FileNodeMap BuildNodeMap(string archivePath)
    {
        var nodeMap = new FileNodeMap();
        var now = (ulong)DateTimeOffset.UtcNow.ToFileTime();

        EnsureRoot(nodeMap, now);

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);

            foreach (var entry in archive.Entries)
            {
                var path = NormalizeEntryPath(entry.Key);
                if (path is null)
                {
                    continue;
                }

                var timestamp = entry.LastModifiedTime is { } lastModified
                    ? (ulong)lastModified.ToUniversalTime().ToFileTimeUtc()
                    : now;

                EnsureAncestorDirectories(nodeMap, path, timestamp);

                if (entry.IsDirectory)
                {
                    EnsureDirectory(nodeMap, path, timestamp);
                    continue;
                }

                AddFile(nodeMap, path, entry, timestamp);
            }
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"Not a supported archive file: {archivePath}", ex);
        }

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
                    total += (ulong)entry.Size;
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

    private static void AddFile(FileNodeMap nodeMap, string path, IArchiveEntry entry, ulong timestamp)
    {
        var size = (ulong)entry.Size;
        var allocationSize = FileNode.AlignToAllocationUnit(size);
        var data = new byte[allocationSize];

        using var entryStream = entry.OpenEntryStream();
        using var target = new MemoryStream(data, 0, (int)size, writable: true);
        entryStream.CopyTo(target);

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
        if (nodeMap.TryGet(path, out _))
        {
            return;
        }

        nodeMap.Add(path, NewDirectoryNode(timestamp));
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