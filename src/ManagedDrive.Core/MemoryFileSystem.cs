using Fsp;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace ManagedDrive.Core;

/// <summary>
/// WinFsp user-mode file system backed entirely by in-memory data structures.
/// Inherits <see cref="FileSystemBase"/> and implements all required callbacks to present a
/// fully functional RAM disk volume to the Windows I/O stack.
/// </summary>
public sealed class MemoryFileSystem : FileSystemBase
{
    private const uint InvalidFileAttributes = FileNode.InvalidFileAttributes;

    private readonly bool _readOnly;
    private volatile bool _isDirty;
    private long _lastContentWriteTicks;
    private ulong _maxCapacity;
    private string _volumeLabel;

    /// <summary>
    /// Initializes a new, empty in-memory file system.
    /// </summary>
    /// <param name="maxCapacity">Maximum capacity of the volume in bytes.</param>
    /// <param name="volumeLabel">NTFS volume label shown in Explorer.</param>
    /// <param name="readOnly">When <c>true</c>, all mutating operations return <c>STATUS_MEDIA_WRITE_PROTECTED</c>.</param>
    public MemoryFileSystem(ulong maxCapacity, string volumeLabel, bool readOnly = false)
    {
        _readOnly = readOnly;
        _maxCapacity = maxCapacity;
        _volumeLabel = volumeLabel;
        NodeMap = new();
    }

    /// <summary>
    /// Initializes an in-memory file system pre-populated from an existing node map
    /// (e.g., when restoring from a persisted image).
    /// </summary>
    /// <param name="maxCapacity">Maximum capacity of the volume in bytes.</param>
    /// <param name="volumeLabel">NTFS volume label shown in Explorer.</param>
    /// <param name="existingNodeMap">Pre-populated node map to use as backing store.</param>
    /// <param name="readOnly">When <c>true</c>, all mutating operations return <c>STATUS_MEDIA_WRITE_PROTECTED</c>.</param>
    public MemoryFileSystem(ulong maxCapacity, string volumeLabel, FileNodeMap existingNodeMap, bool readOnly = false)
    {
        _readOnly = readOnly;
        _maxCapacity = maxCapacity;
        _volumeLabel = volumeLabel;
        NodeMap = existingNodeMap;
    }

    /// <summary>
    /// Gets a value indicating whether the disk's content has changed since the last
    /// successful save (<see cref="ClearDirty"/>).
    /// </summary>
    internal bool IsDirty => _isDirty;

    /// <summary>
    /// Gets the UTC timestamp of the most recent content mutation (create/write/rename/delete/etc.),
    /// or <c>null</c> if the disk's content has never changed since mount.
    /// </summary>
    internal DateTimeOffset? LastContentWriteTimeUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastContentWriteTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Exposes the underlying node map for serialization and capacity queries.
    /// </summary>
    internal FileNodeMap NodeMap
    {
        get;
    }

    /// <summary>
    /// Checks whether a file or directory can be deleted.
    /// Directories must be empty before they may be deleted.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS or STATUS_DIRECTORY_NOT_EMPTY.
    /// </returns>
    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        if (_readOnly)
        {
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        var node = (FileNode)fileNode;

        if (node.IsDirectory)
        {
            foreach (var _ in NodeMap.GetChildren(fileName, null))
            {
                return STATUS_DIRECTORY_NOT_EMPTY;
            }
        }

        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Called when the last handle to a file is closed.
    /// Removes the node from the map if the <c>CleanupDelete</c> flag is set, and
    /// updates timestamps when the corresponding flags are present.
    /// </summary>
    public override void Cleanup(
        object fileNode,
        object fileDesc,
        string fileName,
        uint flags)
    {
        var node = (FileNode)fileNode;
        var now = FileTimeNow();

        if ((flags & CleanupDelete) != 0 && !_readOnly)
        {
            NodeMap.Remove(fileName);
            MarkDirty();
        }

        if ((flags & CleanupSetLastWriteTime) != 0)
        {
            node.FileInfo.LastWriteTime = now;
            node.FileInfo.ChangeTime = now;
            MarkDirty();
        }

        if ((flags & CleanupSetAllocationSize) != 0 && !node.IsDirectory)
        {
            SetFileSizeCore(node, node.FileInfo.FileSize, setAllocationSize: false);
            MarkDirty();
        }
    }

    /// <summary>
    /// Called when all references to an open file have been released. No action required for
    /// an in-memory file system.
    /// </summary>
    public override void Close(object fileNode, object fileDesc)
    {
    }

    /// <summary>
    /// Creates a new file or directory node.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS, STATUS_OBJECT_NAME_COLLISION, or STATUS_DISK_FULL.
    /// </returns>
    public override int Create(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[] securityDescriptor,
        ulong allocationSize,
        out object? fileNode,
        out object? fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = fileName;

        if (_readOnly)
        {
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        if (NodeMap.TryGet(fileName, out _))
        {
            return STATUS_OBJECT_NAME_COLLISION;
        }

        var aligned = FileNode.AlignToAllocationUnit(allocationSize);

        if (NodeMap.GetTotalAllocated() + aligned > _maxCapacity)
        {
            return STATUS_DISK_FULL;
        }

        var now = FileTimeNow();
        var node = new FileNode
        {
            FileSecurity = securityDescriptor,
            FileInfo =
            {
                FileAttributes = fileAttributes,
                AllocationSize = aligned,
                FileSize       = 0,
                CreationTime   = now,
                LastAccessTime = now,
                LastWriteTime  = now,
                ChangeTime     = now,
                IndexNumber    = FileNode.NewIndexNumber(),
            },
        };

        if (aligned > 0 && !node.IsDirectory)
        {
            node.FileData = new byte[aligned];
        }

        NodeMap.Add(fileName, node);
        MarkDirty();
        fileNode = node;
        fileInfo = node.FileInfo;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Flushes file data to stable storage. No-op for an in-memory file system.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS.
    /// </returns>
    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        var node = fileNode as FileNode;
        fileInfo = node?.FileInfo ?? default;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Returns metadata for a single named child of a directory without enumerating all entries.
    /// Called by WinFsp to service efficient single-entry queries.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS or STATUS_OBJECT_NAME_NOT_FOUND.
    /// </returns>
    public override int GetDirInfoByName(
        object fileNode,
        object fileDesc,
        string fileName,
        out string normalizedName,
        out FileInfo fileInfo)
    {
        var dir = (FileNode)fileNode;
        var childPath = dir.FilePath.Length == 1
            ? (dir.FilePath + fileName)
            : (dir.FilePath + "\\" + fileName);

        if (!NodeMap.TryGet(childPath, out var child) || child == null)
        {
            normalizedName = fileName;
            fileInfo = default;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        normalizedName = fileName;
        fileInfo = child.FileInfo;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Returns the current metadata for a file or directory.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS.
    /// </returns>
    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        var node = (FileNode)fileNode;
        fileInfo = node.FileInfo;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Returns the security descriptor for a file or directory.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS.
    /// </returns>
    public override int GetSecurity(
        object fileNode,
        object fileDesc,
        ref byte[] securityDescriptor)
    {
        var node = (FileNode)fileNode;
        securityDescriptor = node.FileSecurity ?? Array.Empty<byte>();
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Looks up a path and returns its file attributes and, optionally, its security descriptor.
    /// Called by WinFsp during Create/Open to resolve the target path before the operation.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS or STATUS_OBJECT_NAME_NOT_FOUND.
    /// </returns>
    public override int GetSecurityByName(
        string fileName,
        out uint fileAttributes,
        ref byte[] securityDescriptor)
    {
        if (!NodeMap.TryGet(fileName, out var node) || node == null)
        {
            fileAttributes = 0;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        fileAttributes = node.FileInfo.FileAttributes;

        if (securityDescriptor != null)
        {
            securityDescriptor = node.FileSecurity;
        }

        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Provides volume size and label to the WinFsp framework.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS.
    /// </returns>
    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        var used = NodeMap.GetTotalAllocated();
        volumeInfo = default;
        volumeInfo.TotalSize = _maxCapacity;
        volumeInfo.FreeSize = _maxCapacity > used ? _maxCapacity - used : 0;
        volumeInfo.SetVolumeLabel(_volumeLabel);
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Called by WinFsp after the file system host is initialized. Creates the root directory
    /// if it does not already exist.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS.
    /// </returns>
    public override int Init(object host)
    {
        if (!NodeMap.TryGet("\\", out _))
        {
            var now = FileTimeNow();

            var root = new FileNode
            {
                FileSecurity = FileNode.DefaultSecurityDescriptorBytes,
                FileInfo =
                {
                    FileAttributes = (uint)FileAttributes.Directory,
                    CreationTime   = now,
                    LastAccessTime = now,
                    LastWriteTime  = now,
                    ChangeTime     = now,
                    IndexNumber    = FileNode.NewIndexNumber(),
                },
            };
            NodeMap.Add("\\", root);
        }

        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Opens an existing file or directory node.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS or STATUS_OBJECT_NAME_NOT_FOUND.
    /// </returns>
    public override int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out object? fileNode,
        out object? fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = fileName;

        if (!NodeMap.TryGet(fileName, out var node) || node == null)
        {
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        fileNode = node;
        fileInfo = node.FileInfo;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Overwrites an existing file, either replacing or merging its file attributes,
    /// then resets its content to zero length.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS or STATUS_DISK_FULL.
    /// </returns>
    public override int Overwrite(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out FileInfo fileInfo)
    {
        if (_readOnly)
        {
            fileInfo = default;
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        var node = (FileNode)fileNode;
        var aligned = FileNode.AlignToAllocationUnit(allocationSize);
        var currentAlloc = node.FileInfo.AllocationSize;
        var extra = aligned > currentAlloc ? aligned - currentAlloc : 0;

        if (NodeMap.GetTotalAllocated() + extra > _maxCapacity)
        {
            fileInfo = node.FileInfo;
            return STATUS_DISK_FULL;
        }

        if (replaceFileAttributes)
        {
            node.FileInfo.FileAttributes = fileAttributes;
        }
        else
        {
            node.FileInfo.FileAttributes |= fileAttributes;
        }

        NodeMap.UpdateAllocationSize(node, aligned);
        node.FileInfo.FileSize = 0;
        node.FileData = aligned > 0 ? new byte[aligned] : null;

        var now = FileTimeNow();
        node.FileInfo.LastAccessTime = now;
        node.FileInfo.LastWriteTime = now;
        node.FileInfo.ChangeTime = now;

        MarkDirty();
        fileInfo = node.FileInfo;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Reads data from a file into the caller-supplied buffer.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS, or STATUS_END_OF_FILE if the offset is past the end of the file.
    /// </returns>
    public override int Read(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred)
    {
        var node = (FileNode)fileNode;
        bytesTransferred = 0;

        if (offset >= node.FileInfo.FileSize)
        {
            return STATUS_END_OF_FILE;
        }

        var remaining = node.FileInfo.FileSize - offset;
        var toRead = (uint)Math.Min(length, remaining);

        if (toRead > 0 && node.FileData != null)
        {
            Marshal.Copy(node.FileData, (int)offset, buffer, (int)toRead);
            bytesTransferred = toRead;
        }

        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Returns the next directory entry during a <c>ReadDirectory</c> operation.
    /// On the first call (<paramref name="context"/> is <c>null</c>), a complete, snapshot-based
    /// list of entries is built (including <c>.</c> and <c>..</c>), filtered by
    /// <paramref name="pattern"/> and positioned after <paramref name="marker"/>.
    /// Subsequent calls advance through the same list.
    /// </summary>
    /// <returns>
    /// <c>true</c> if an entry was written to <paramref name="fileName"/> and
    /// <paramref name="fileInfo"/>; <c>false</c> when enumeration is complete.
    /// </returns>
    public override bool ReadDirectoryEntry(
        object fileNode,
        object fileDesc,
        string? pattern,
        string? marker,
        ref object? context,
        out string? fileName,
        out FileInfo fileInfo)
    {
        context ??= BuildDirContext((FileNode)fileNode, pattern, marker);

        return ((DirContext)context).TryNext(out fileName, out fileInfo);
    }

    /// <summary>
    /// Renames a file or directory. When the target already exists,
    /// it is replaced only if <paramref name="replaceIfExists"/> is <c>true</c>.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS or STATUS_OBJECT_NAME_COLLISION.
    /// </returns>
    public override int Rename(
        object fileNode,
        object fileDesc,
        string fileName,
        string newFileName,
        bool replaceIfExists)
    {
        if (_readOnly)
        {
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        if (NodeMap.TryGet(newFileName, out var existing) && existing != null)
        {
            if (!replaceIfExists)
            {
                return STATUS_OBJECT_NAME_COLLISION;
            }

            NodeMap.Remove(newFileName);
        }

        var node = (FileNode)fileNode;

        if (node.IsDirectory)
        {
            NodeMap.RenameDescendants(fileName, newFileName);
        }

        NodeMap.Remove(fileName);
        NodeMap.Add(newFileName, node);
        MarkDirty();
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Sets file attributes and timestamps. A field is unchanged when its value is zero
    /// (or <see cref="FileNode.InvalidFileAttributes"/> for attributes).
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS.
    /// </returns>
    public override int SetBasicInfo(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out FileInfo fileInfo)
    {
        if (_readOnly)
        {
            fileInfo = default;
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        var node = (FileNode)fileNode;

        if (fileAttributes != InvalidFileAttributes)
        {
            node.FileInfo.FileAttributes = fileAttributes;
        }

        if (creationTime != 0)
        {
            node.FileInfo.CreationTime = creationTime;
        }
        if (lastAccessTime != 0)
        {
            node.FileInfo.LastAccessTime = lastAccessTime;
        }
        if (lastWriteTime != 0)
        {
            node.FileInfo.LastWriteTime = lastWriteTime;
        }
        if (changeTime != 0)
        {
            node.FileInfo.ChangeTime = changeTime;
        }

        MarkDirty();
        fileInfo = node.FileInfo;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Sets the file size or allocation size.
    /// When <paramref name="setAllocationSize"/> is <c>true</c>, the allocation buffer is resized
    /// and the file size is clamped if it would exceed the new allocation.
    /// When <c>false</c>, the logical file size is updated and the allocation grows if needed.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS or STATUS_DISK_FULL.
    /// </returns>
    public override int SetFileSize(
        object fileNode,
        object fileDesc,
        ulong newSize,
        bool setAllocationSize,
        out FileInfo fileInfo)
    {
        if (_readOnly)
        {
            fileInfo = default;
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        var node = (FileNode)fileNode;
        var result = SetFileSizeCore(node, newSize, setAllocationSize);
        MarkDirty();
        fileInfo = node.FileInfo;
        return result;
    }

    /// <summary>
    /// Sets (replaces or merges) the security descriptor for a file or directory.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS.
    /// </returns>
    public override int SetSecurity(
        object fileNode,
        object fileDesc,
        AccessControlSections sections,
        byte[] securityDescriptor)
    {
        if (_readOnly)
        {
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        var node = (FileNode)fileNode;
        node.FileSecurity = securityDescriptor;
        MarkDirty();
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Updates the volume label.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS.
    /// </returns>
    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        if (_readOnly)
        {
            volumeInfo = default;
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        _volumeLabel = volumeLabel;
        MarkDirty();
        return GetVolumeInfo(out volumeInfo);
    }

    /// <summary>
    /// Writes data from the caller-supplied buffer into a file, extending it if necessary.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS or STATUS_DISK_FULL.
    /// </returns>
    public override int Write(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out FileInfo fileInfo)
    {
        if (_readOnly)
        {
            bytesTransferred = 0;
            fileInfo = default;
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        var node = (FileNode)fileNode;
        bytesTransferred = 0;
        fileInfo = node.FileInfo;

        var writeOffset = writeToEndOfFile ? node.FileInfo.FileSize : offset;

        if (constrainedIo)
        {
            if (writeOffset >= node.FileInfo.FileSize)
            {
                return STATUS_SUCCESS;
            }

            var available = node.FileInfo.FileSize - writeOffset;
            length = (uint)Math.Min(length, available);
        }

        var writeEnd = writeOffset + length;

        if (writeEnd > node.FileInfo.FileSize)
        {
            var result = SetFileSizeCore(node, writeEnd, setAllocationSize: false);
            if (result != STATUS_SUCCESS)
            {
                return result;
            }
        }

        if (length > 0 && node.FileData != null)
        {
            Marshal.Copy(buffer, node.FileData, (int)writeOffset, (int)length);
        }

        bytesTransferred = length;

        var now = FileTimeNow();
        node.FileInfo.LastAccessTime = now;
        node.FileInfo.LastWriteTime = now;
        node.FileInfo.ChangeTime = now;

        MarkDirty();
        fileInfo = node.FileInfo;
        return STATUS_SUCCESS;
    }

    internal static bool WildcardMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name)
    {
        var p = 0;
        var n = 0;
        var starIdx = -1;
        var matchIdx = 0;

        while (n < name.Length)
        {
            if (p < pattern.Length &&
                (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p;
                matchIdx = n;
                p++;
            }
            else if (starIdx != -1)
            {
                p = starIdx + 1;
                matchIdx++;
                n = matchIdx;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>
    /// Marks the disk's content as up to date with the on-disk image.
    /// </summary>
    internal void ClearDirty() => _isDirty = false;

    /// <summary>
    /// Marks the disk's content as changed since the last save.
    /// </summary>
    internal void MarkDirty()
    {
        _isDirty = true;
        Interlocked.Exchange(ref _lastContentWriteTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>
    /// Replaces this file system's entire contents with a deep copy of <paramref name="sourceMap"/>.
    /// Used to clone one mounted disk's contents onto another. Fails without modifying this
    /// file system when it is read-only or when the source's allocated bytes exceed this
    /// file system's capacity.
    /// </summary>
    /// <param name="sourceMap">The node map to copy from.</param>
    /// <param name="error">Set to a human-readable message when the method returns <c>false</c>.</param>
    /// <returns>
    /// <c>true</c> on success; <c>false</c> when the disk is read-only or too small.
    /// </returns>
    internal bool TryReplaceContents(FileNodeMap sourceMap, out string? error)
    {
        if (_readOnly)
        {
            error = "Cannot clone into a read-only disk.";
            return false;
        }

        var needed = sourceMap.GetTotalAllocated();
        if (needed > _maxCapacity)
        {
            error = $"Source disk uses {needed:N0} bytes, which exceeds the target disk's capacity ({_maxCapacity:N0} bytes).";
            return false;
        }

        NodeMap.ClearAll();
        foreach (var kvp in sourceMap.GetAllNodes())
        {
            NodeMap.Add(kvp.Key, kvp.Value.Clone());
        }

        MarkDirty();
        error = null;
        return true;
    }

    /// <summary>
    /// Attempts to update the capacity ceiling.
    /// Returns <c>false</c> if the new capacity is smaller than the bytes currently allocated.
    /// </summary>
    internal bool TryUpdateCapacity(ulong newCapacity)
    {
        if (NodeMap.GetTotalAllocated() > newCapacity)
        {
            return false;
        }

        _maxCapacity = newCapacity;
        return true;
    }

    /// <summary>
    /// Updates the volume label reported by <see cref="GetVolumeInfo"/>.
    /// </summary>
    internal void UpdateVolumeLabel(string label) => _volumeLabel = label;

    private static ulong FileTimeNow() => (ulong)DateTimeOffset.UtcNow.ToFileTime();

    /// <summary>
    /// Returns <c>true</c> when <paramref name="name"/> matches the glob
    /// <paramref name="pattern"/> (case-insensitive, supporting <c>*</c> and <c>?</c>).
    /// A null or <c>*</c> pattern matches everything.
    /// </summary>
    private static bool MatchesPattern(string? pattern, string name)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
        {
            return true;
        }

        return WildcardMatch(pattern.AsSpan(), name.AsSpan());
    }

    /// <summary>
    /// Builds the full directory-entry list for <paramref name="dir"/> at the start of a
    /// <c>ReadDirectoryEntry</c> sequence, including <c>.</c> and <c>..</c>.
    /// </summary>
    private DirContext BuildDirContext(FileNode dir, string? pattern, string? marker)
    {
        var entries = new List<(string Name, FileInfo Info)>();

        // "." — current directory
        var addDot = marker == null ||
                     string.Compare(".", marker, StringComparison.OrdinalIgnoreCase) > 0;
        if (addDot)
        {
            entries.Add((".", dir.FileInfo));
        }

        // ".." — parent directory
        var addDotDot = marker == null ||
                        string.Compare("..", marker, StringComparison.OrdinalIgnoreCase) > 0;
        if (addDotDot)
        {
            var parentNode = dir;
            if (dir.FilePath.Length > 1)
            {
                var parentPath = Path.GetDirectoryName(dir.FilePath)!;
                if (!NodeMap.TryGet(parentPath, out var p) || p == null)
                {
                    parentNode = dir;
                }
                else
                {
                    parentNode = p;
                }
            }

            entries.Add(("..", parentNode.FileInfo));
        }

        // Pass marker to GetChildren only when it names a real child (i.e., it follows "..")
        string? childMarker = null;
        if (marker != null &&
            string.Compare(marker, "..", StringComparison.OrdinalIgnoreCase) > 0)
        {
            childMarker = marker;
        }

        foreach (var kvp in NodeMap.GetChildren(dir.FilePath, childMarker))
        {
            var childName = kvp.Value.LeafName;
            if (MatchesPattern(pattern, childName))
            {
                entries.Add((childName, kvp.Value.FileInfo));
            }
        }

        return new(entries);
    }

    /// <summary>
    /// Core implementation for both file-size and allocation-size changes.
    /// When <paramref name="setAllocationSize"/> is <c>true</c>, resizes the backing buffer and
    /// clamps FileSize. When <c>false</c>, extends/truncates FileSize and grows allocation
    /// if needed.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS or STATUS_DISK_FULL.
    /// </returns>
    private int SetFileSizeCore(FileNode node, ulong newSize, bool setAllocationSize)
    {
        if (setAllocationSize)
        {
            var aligned = FileNode.AlignToAllocationUnit(newSize);

            if (aligned == node.FileInfo.AllocationSize)
            {
                return STATUS_SUCCESS;
            }

            if (aligned > node.FileInfo.AllocationSize)
            {
                var extra = aligned - node.FileInfo.AllocationSize;
                if (NodeMap.GetTotalAllocated() + extra > _maxCapacity)
                {
                    return STATUS_DISK_FULL;
                }
            }

            if (aligned > 0)
            {
                var newBuffer = new byte[aligned];
                if (node.FileData != null)
                {
                    Buffer.BlockCopy(
                        node.FileData, 0,
                        newBuffer, 0,
                        (int)Math.Min((ulong)node.FileData.Length, aligned));
                }

                node.FileData = newBuffer;
            }
            else
            {
                node.FileData = null;
            }

            NodeMap.UpdateAllocationSize(node, aligned);

            if (node.FileInfo.FileSize > aligned)
            {
                node.FileInfo.FileSize = aligned;
            }
        }
        else
        {
            if (newSize == node.FileInfo.FileSize)
            {
                return STATUS_SUCCESS;
            }

            if (newSize > node.FileInfo.AllocationSize)
            {
                var result = SetFileSizeCore(node, newSize, setAllocationSize: true);
                if (result != STATUS_SUCCESS)
                {
                    return result;
                }
            }

            node.FileInfo.FileSize = newSize;
        }

        return STATUS_SUCCESS;
    }

    private sealed class DirContext
    {
        private readonly List<(string Name, FileInfo Info)> _entries;
        private int _index;

        internal DirContext(List<(string Name, FileInfo Info)> entries)
        {
            _entries = entries;
            _index = 0;
        }

        /// <summary>
        /// Advances to the next entry.
        /// </summary>
        /// <returns>
        /// <c>true</c> if an entry was written; <c>false</c> when the list is exhausted.
        /// </returns>
        internal bool TryNext(out string? name, out FileInfo info)
        {
            if (_index < _entries.Count)
            {
                name = _entries[_index].Name;
                info = _entries[_index].Info;
                _index++;
                return true;
            }

            name = null;
            info = default;
            return false;
        }
    }
}