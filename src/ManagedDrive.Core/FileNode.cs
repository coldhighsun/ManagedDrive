namespace ManagedDrive.Core;

/// <summary>
/// Represents a single file or directory node in the in-memory file system.
/// </summary>
public sealed class FileNode
{
    /// <summary>
    /// In-memory content buffer. <c>null</c> for directories.
    /// Its logical length is <see cref="Fsp.Interop.FileInfo.AllocationSize"/>;
    /// the valid data range is <c>[0, FileInfo.FileSize)</c>.
    /// </summary>
    public byte[]? FileData;

    /// <summary>
    /// WinFsp file metadata: attributes, sizes, timestamps, and index number.
    /// </summary>
    public Fsp.Interop.FileInfo FileInfo;

    /// <summary>
    /// Raw NTFS security descriptor bytes for this node.
    /// </summary>
    public byte[]? FileSecurity;

    /// <summary>
    /// The allocation granularity in bytes. All allocation sizes are rounded up to this boundary.
    /// </summary>
    internal const ulong AllocationUnit = 512;

    /// <summary>
    /// Sentinel value passed by WinFsp to <c>SetBasicInfo</c> to indicate that file attributes
    /// should not be changed.
    /// </summary>
    internal const uint InvalidFileAttributes = 0xFFFF_FFFF;

    private static long _nextIndex = 1;

    /// <summary>
    /// The full path of this node within the virtual file system (e.g., <c>\Folder\File.txt</c>).
    /// Kept in sync by <see cref="FileNodeMap"/>.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether this node represents a directory.
    /// </summary>
    public bool IsDirectory =>
        (FileInfo.FileAttributes & (uint)FileAttributes.Directory) != 0;

    /// <summary>
    /// The name component of <see cref="FilePath"/> (i.e., the path's last segment).
    /// Kept in sync by <see cref="FileNodeMap"/> whenever <see cref="FilePath"/> changes.
    /// </summary>
    public string LeafName { get; set; } = string.Empty;

    /// <summary>
    /// Rounds <paramref name="size"/> up to the nearest <see cref="AllocationUnit"/> boundary.
    /// </summary>
    /// <param name="size">The size in bytes to align.</param>
    /// <returns>
    /// The smallest multiple of <see cref="AllocationUnit"/> that is &gt;= <paramref name="size"/>.
    /// </returns>
    public static ulong AlignToAllocationUnit(ulong size)
    {
        return (size + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
    }

    /// <summary>
    /// Returns a new globally unique index number to assign to a newly created node.
    /// </summary>
    /// <returns>
    /// A unique, monotonically increasing 64-bit index number.
    /// </returns>
    public static ulong NewIndexNumber()
    {
        return (ulong)Interlocked.Increment(ref _nextIndex);
    }

    /// <summary>
    /// Returns a deep copy of this node (independent <see cref="FileData"/> and
    /// <see cref="FileSecurity"/> buffers), for copying a node into a different
    /// <see cref="FileNodeMap"/> without the two nodes sharing mutable state.
    /// </summary>
    /// <returns>
    /// A new <see cref="FileNode"/> with the same metadata and a copy of the data buffers.
    /// </returns>
    public FileNode Clone()
    {
        return new FileNode
        {
            FileInfo = FileInfo,
            FileSecurity = FileSecurity?.ToArray(),
            FileData = FileData?.ToArray(),
            FilePath = FilePath,
            LeafName = LeafName,
        };
    }
}