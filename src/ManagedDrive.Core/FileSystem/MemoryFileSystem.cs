using Fsp;
using System.Buffers.Binary;
using System.Security.AccessControl;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace ManagedDrive.Core.FileSystem;

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
    private ContentAccessInfo? _lastContentReadAccess;
    private long _lastContentReadTicks;
    private ContentAccessInfo? _lastContentWriteAccess;
    private long _lastContentWriteTicks;
    private ulong _maxCapacity;
    private long _totalBytesRead;
    private long _totalBytesWritten;
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
    /// Raised whenever a file's content is read or written, with <c>true</c> for writes and
    /// <c>false</c> for reads. Fired synchronously from WinFsp driver threads (concurrent,
    /// potentially high-frequency) — subscribers must not assume the UI thread and must handle
    /// their own thread safety.
    /// </summary>
    internal event Action<bool>? ContentAccessed;

    /// <summary>
    /// Gets a value indicating whether the disk's content has changed since the last
    /// successful save (<see cref="ClearDirty"/>).
    /// </summary>
    internal bool IsDirty => _isDirty;

    /// <summary>
    /// Gets an atomic snapshot of the most recent successful <see cref="Read"/> of file content
    /// (time + path), or <c>null</c> if the disk has never been read from since mount.
    /// </summary>
    internal ContentAccessInfo? LastContentReadAccess => Volatile.Read(ref _lastContentReadAccess);

    /// <summary>
    /// Gets the UTC timestamp of the most recent successful <see cref="Read"/> of file content,
    /// or <c>null</c> if the disk has never been read from since mount.
    /// </summary>
    internal DateTimeOffset? LastContentReadTimeUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastContentReadTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Gets an atomic snapshot of the most recent successful <see cref="Write"/> of file content
    /// (time + path), or <c>null</c> if the disk has never been written to since mount. Unlike
    /// <see cref="LastContentWriteTimeUtc"/>, this only reflects actual content writes, not other
    /// mutations (rename/delete/attribute changes/etc.) that also call <see cref="MarkDirty"/>.
    /// </summary>
    internal ContentAccessInfo? LastContentWriteAccess => Volatile.Read(ref _lastContentWriteAccess);

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
    /// Gets the cumulative number of bytes read from file content since mount. Never resets;
    /// consumers derive a rate by sampling the delta between two reads of this value over time.
    /// </summary>
    internal long TotalBytesRead => Interlocked.Read(ref _totalBytesRead);

    /// <summary>
    /// Gets the cumulative number of bytes written to file content since mount. Never resets;
    /// consumers derive a rate by sampling the delta between two reads of this value over time.
    /// </summary>
    internal long TotalBytesWritten => Interlocked.Read(ref _totalBytesWritten);

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

        if (WouldExceedCapacity(aligned))
        {
            return STATUS_DISK_FULL;
        }

        var now = FileTimeNow();
        var node = new FileNode
        {
            FileSecurity = securityDescriptor is { Length: > 0 }
                ? securityDescriptor
                : FileNode.DefaultSecurityDescriptorBytes,
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
            node.FileData = FileContent.CreateZeroed(aligned);
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
        securityDescriptor = EffectiveSecurity(node);
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
            securityDescriptor = EffectiveSecurity(node);
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

        if (WouldExceedCapacity(extra))
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
        node.FileData = aligned > 0 ? FileContent.CreateZeroed(aligned) : null;
        node.ContentVersion++;

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
            node.FileData.ReadTo(offset, buffer, toRead);
            bytesTransferred = toRead;
            Interlocked.Add(ref _totalBytesRead, toRead);
            var readNow = DateTimeOffset.UtcNow;
            Interlocked.Exchange(ref _lastContentReadTicks, readNow.UtcTicks);
            Interlocked.Exchange(ref _lastContentReadAccess, new(readNow, node.FilePath));
            ContentAccessed?.Invoke(false);
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
        context ??= DirectoryEnumeration.Build(NodeMap, (FileNode)fileNode, pattern, marker);

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
    /// Merges the requested modifications into the node's security descriptor. WinFsp passes a
    /// <em>modification</em> descriptor, not a complete replacement — it must be combined with the
    /// node's existing descriptor via <see cref="ModifySecurityDescriptorEx"/>. Storing the
    /// modification descriptor verbatim leaves the node with a descriptor the kernel rejects, so
    /// every later open (including a delete) fails with STATUS_INVALID_SECURITY_DESCR.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS or an error code from the merge.
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

        byte[] merged = [];
        var result = ModifySecurityDescriptorEx(
            EffectiveSecurity(node),
            sections,
            securityDescriptor,
            ref merged);

        if (result != STATUS_SUCCESS)
        {
            return result;
        }

        node.FileSecurity = merged;
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
            node.FileData.WriteFrom(buffer, writeOffset, length);
            node.ContentVersion++;
        }

        bytesTransferred = length;
        Interlocked.Add(ref _totalBytesWritten, length);

        var nowOffset = DateTimeOffset.UtcNow;
        var now = (ulong)nowOffset.ToFileTime();
        node.FileInfo.LastAccessTime = now;
        node.FileInfo.LastWriteTime = now;
        node.FileInfo.ChangeTime = now;

        MarkDirty(nowOffset);
        Interlocked.Exchange(ref _lastContentWriteAccess, new(nowOffset, node.FilePath));
        fileInfo = node.FileInfo;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Marks the disk's content as up to date with the on-disk image.
    /// </summary>
    internal void ClearDirty() => _isDirty = false;

    /// <summary>
    /// Marks the disk's content as changed since the last save.
    /// </summary>
    internal void MarkDirty() => MarkDirty(DateTimeOffset.UtcNow);

    /// <summary>
    /// Marks the disk's content as changed since the last save, using a caller-supplied
    /// timestamp to avoid redundant <see cref="DateTimeOffset.UtcNow"/> calls on hot paths
    /// that already captured "now" for other purposes.
    /// </summary>
    private void MarkDirty(DateTimeOffset now)
    {
        _isDirty = true;
        Interlocked.Exchange(ref _lastContentWriteTicks, now.UtcTicks);
        ContentAccessed?.Invoke(true);
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

    /// <summary>
    /// Returns the security descriptor WinFsp should see for <paramref name="node"/>. Nodes that
    /// carry no security information (e.g. loaded from an image whose entries stored a zero-length
    /// descriptor) must fall back to the default descriptor: handing WinFsp an empty buffer makes
    /// its access check fail with STATUS_INVALID_SECURITY_DESCR, which surfaces as
    /// "The security descriptor structure is invalid" on any open/delete of that node.
    /// </summary>
    private static byte[] EffectiveSecurity(FileNode node) =>
        IsValidSelfRelativeSecurityDescriptor(node.FileSecurity)
            ? node.FileSecurity!
            : FileNode.DefaultSecurityDescriptorBytes;

    private static ulong FileTimeNow() => (ulong)DateTimeOffset.UtcNow.ToFileTime();

    /// <summary>
    /// Checks that a descriptor is usable for WinFsp's access check, which runs in user mode via
    /// the Win32 <c>AccessCheck</c> API: it must be a self-relative, revision-1 descriptor that
    /// carries both an owner and a group SID — <c>AccessCheck</c> fails an ownerless or groupless
    /// descriptor with ERROR_INVALID_SECURITY_DESCR (1338, "the security descriptor structure is
    /// invalid"). This also rejects the DACL-only modification descriptors that earlier builds'
    /// <c>SetSecurity</c> stored verbatim (including any already persisted into a <c>.mdr</c>
    /// image), so those nodes fall back to the default descriptor and become openable/deletable
    /// again instead of failing every open.
    /// </summary>
    private static bool IsValidSelfRelativeSecurityDescriptor(byte[]? securityDescriptor)
    {
        const int HeaderLength = 20;
        const ushort SeSelfRelative = 0x8000;

        if (securityDescriptor is not { Length: >= HeaderLength } sd || sd[0] != 1)
        {
            return false;
        }

        var control = (ushort)(sd[2] | (sd[3] << 8));

        if ((control & SeSelfRelative) == 0)
        {
            return false;
        }

        var ownerOffset = BinaryPrimitives.ReadUInt32LittleEndian(sd.AsSpan(4));
        var groupOffset = BinaryPrimitives.ReadUInt32LittleEndian(sd.AsSpan(8));

        return ownerOffset is > 0 and < int.MaxValue
            && groupOffset is > 0 and < int.MaxValue
            && ownerOffset < (uint)sd.Length
            && groupOffset < (uint)sd.Length;
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
                if (WouldExceedCapacity(extra))
                {
                    return STATUS_DISK_FULL;
                }
            }

            if (aligned > 0)
            {
                if (node.FileData != null)
                {
                    node.FileData.Resize(aligned);
                }
                else
                {
                    node.FileData = FileContent.CreateZeroed(aligned);
                }
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

        node.ContentVersion++;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Returns <c>true</c> when allocating <paramref name="extra"/> more bytes on top of the
    /// currently allocated total would exceed the volume's capacity ceiling.
    /// </summary>
    private bool WouldExceedCapacity(ulong extra) => NodeMap.GetTotalAllocated() + extra > _maxCapacity;
}