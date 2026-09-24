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

    /// <summary>
    /// Low-memory write guard: refuses an operation that would leave less than
    /// <see cref="MemoryHeadroomBudget.ReserveBytes"/> of physical memory available system-wide,
    /// independent of the volume's capacity (a disk can have capacity headroom while the machine
    /// is nearly out of RAM). It is charged inside <see cref="FileContent.TryWriteFrom"/> and
    /// <see cref="FileContent.TryResize"/> with the backing arrays an operation really allocates,
    /// not its growth in allocation size: that growth is sparse, so charging it would both deny
    /// preallocations that cost nothing and let later writes into the preallocated range go
    /// unguarded.
    /// </summary>
    private readonly MemoryHeadroomBudget _memoryBudget;
    private readonly bool _readOnly;

    /// <summary>
    /// Monotonically increasing counter bumped by every mutation (see <see cref="MarkDirty()"/>).
    /// Compared against <see cref="_savedVersion"/> to determine <see cref="IsDirty"/>.
    /// </summary>
    private long _mutationVersion;

    /// <summary>
    /// The <see cref="_mutationVersion"/> value as of the last successful save
    /// (<see cref="ClearDirtySince"/>).
    /// </summary>
    private long _savedVersion;

    private string? _lastContentReadPath;
    private string? _lastContentWritePath;
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
    /// <param name="availableMemoryProvider">
    /// Overrides the source of "currently available physical memory" used by the low-memory
    /// write guard, for test injection; such a file system gets its own private budget. Defaults to
    /// <see cref="SystemMemoryInfo.GetAvailablePhysicalBytes"/> via the process-wide
    /// <see cref="MemoryHeadroomBudget.Shared"/>.
    /// </param>
    public MemoryFileSystem(ulong maxCapacity, string volumeLabel, bool readOnly = false, Func<ulong>? availableMemoryProvider = null)
    {
        _readOnly = readOnly;
        _maxCapacity = maxCapacity;
        _volumeLabel = volumeLabel;
        _memoryBudget = availableMemoryProvider == null ? MemoryHeadroomBudget.Shared : new(availableMemoryProvider);
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
    /// <param name="availableMemoryProvider">
    /// Overrides the source of "currently available physical memory" used by the low-memory
    /// write guard, for test injection; such a file system gets its own private budget. Defaults to
    /// <see cref="SystemMemoryInfo.GetAvailablePhysicalBytes"/> via the process-wide
    /// <see cref="MemoryHeadroomBudget.Shared"/>.
    /// </param>
    public MemoryFileSystem(ulong maxCapacity, string volumeLabel, FileNodeMap existingNodeMap, bool readOnly = false, Func<ulong>? availableMemoryProvider = null)
    {
        _readOnly = readOnly;
        _maxCapacity = maxCapacity;
        _volumeLabel = volumeLabel;
        _memoryBudget = availableMemoryProvider == null ? MemoryHeadroomBudget.Shared : new(availableMemoryProvider);
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
    /// successful save (<see cref="ClearDirtySince"/>).
    /// </summary>
    internal bool IsDirty => Interlocked.Read(ref _mutationVersion) != Interlocked.Read(ref _savedVersion);

    /// <summary>
    /// Gets the path of the file most recently read via <see cref="Read"/>, or <c>null</c> if the
    /// disk has never been read from since mount. Best-effort, for UI display only.
    /// </summary>
    internal string? LastContentReadPath => Volatile.Read(ref _lastContentReadPath);

    /// <summary>
    /// Gets the path of the file most recently written via <see cref="Write"/>, or <c>null</c> if
    /// the disk has never been written to since mount. Best-effort, for UI display only.
    /// </summary>
    internal string? LastContentWritePath => Volatile.Read(ref _lastContentWritePath);

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

        if (node.IsDirectory && NodeMap.HasChildren(fileName))
        {
            return STATUS_DIRECTORY_NOT_EMPTY;
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

        var deleted = (flags & CleanupDelete) != 0 && !_readOnly;
        if (deleted)
        {
            // Only this handle's own node: after a format or restore swapped it out, another node
            // may now live at the same path, and it must not be deleted in its place. A directory
            // that gained a child since CanDelete approved it is kept rather than orphaning it.
            NodeMap.TryDelete(fileName, node);
            MarkDirty();
        }

        if ((flags & CleanupSetLastWriteTime) != 0)
        {
            node.FileInfo.LastWriteTime = now;
            node.FileInfo.ChangeTime = now;
            node.MetadataVersion++;
            MarkDirty();
        }

        // Skipped for a node deleted above: it's no longer in the map, so trimming it would
        // subtract its allocation from the map's cached total a second time.
        if ((flags & CleanupSetAllocationSize) != 0 && !node.IsDirectory && !_readOnly && !deleted)
        {
            // WinFsp asks for the allocation to be trimmed to the file size on close, releasing
            // space preallocated but never written. Only a real change marks the disk dirty.
            var versionBefore = node.ContentVersion;
            SetFileSizeCore(node, node.FileInfo.FileSize, setAllocationSize: true);
            if (node.ContentVersion != versionBefore)
            {
                MarkDirty();
            }
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
    /// STATUS_SUCCESS, STATUS_OBJECT_NAME_COLLISION, STATUS_OBJECT_PATH_NOT_FOUND,
    /// STATUS_NOT_A_DIRECTORY, or STATUS_DISK_FULL.
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
            // Cheap pre-check so a request that obviously exceeds capacity is rejected before
            // paying for CreateZeroed's chunk-pointer allocation (proportional to aligned) below.
            // Not a substitute for the atomic check below — two concurrent creates can still both
            // read a stale total here and pass — just a fast path that avoids the wasted
            // allocation in the common (non-racing) over-capacity case.
            if (NodeMap.GetTotalAllocated() + aligned > _maxCapacity)
            {
                return STATUS_DISK_FULL;
            }

            node.FileData = FileContent.CreateZeroed(aligned);
        }

        // Checking for a collision, the parent directory, and headroom, and adding the node, all
        // happen under one NodeMap write-lock acquisition (see TryCreate): a concurrent create of
        // the same name can't be replaced, a node can't land under a directory being deleted, and
        // two concurrent creates can't both pass a stale capacity check.
        switch (NodeMap.TryCreate(fileName, node, _maxCapacity))
        {
            case FileNodeMap.CreateResult.NameCollision:
                return STATUS_OBJECT_NAME_COLLISION;
            case FileNodeMap.CreateResult.ParentNotFound:
                return STATUS_OBJECT_PATH_NOT_FOUND;
            case FileNodeMap.CreateResult.ParentNotDirectory:
                return STATUS_NOT_A_DIRECTORY;
            case FileNodeMap.CreateResult.CapacityExceeded:
                return STATUS_DISK_FULL;
        }

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

        // Checking headroom and applying the growth happen atomically (see
        // TryUpdateAllocationSizeWithinCapacity), so two concurrent extending writes on different
        // nodes can't both pass a stale capacity check and together push the real total past
        // _maxCapacity.
        if (!NodeMap.TryUpdateAllocationSizeWithinCapacity(node, aligned, _maxCapacity))
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
            RecordLastPath(ref _lastContentReadPath, node.FilePath);
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
    /// STATUS_SUCCESS, STATUS_OBJECT_NAME_COLLISION, STATUS_DIRECTORY_NOT_EMPTY,
    /// STATUS_ACCESS_DENIED (renaming a directory into its own subtree), or
    /// STATUS_FILE_IS_A_DIRECTORY/STATUS_NOT_A_DIRECTORY (replacing across the file/directory kind).
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

        var node = (FileNode)fileNode;

        if (node.IsDirectory &&
            newFileName.Length > fileName.Length &&
            newFileName.StartsWith(fileName, StringComparison.OrdinalIgnoreCase) &&
            newFileName[fileName.Length] == '\\')
        {
            // Can't move a directory into its own subtree.
            return STATUS_ACCESS_DENIED;
        }

        // Checking for a colliding target and moving the node both happen under one NodeMap.Rename
        // write-lock acquisition, so a concurrent Create/Rename on another WinFsp driver thread
        // can't interleave with this — e.g. land a new child between a "target directory is empty"
        // check and the subtree removal that would otherwise silently delete it.
        var conflict = NodeMap.Rename(fileName, newFileName, node, replaceIfExists);
        switch (conflict)
        {
            case FileNodeMap.RenameConflict.NameCollision:
                return STATUS_OBJECT_NAME_COLLISION;
            case FileNodeMap.RenameConflict.TargetIsDirectory:
                return STATUS_FILE_IS_A_DIRECTORY;
            case FileNodeMap.RenameConflict.TargetIsFile:
                return STATUS_NOT_A_DIRECTORY;
            case FileNodeMap.RenameConflict.DirectoryNotEmpty:
                return STATUS_DIRECTORY_NOT_EMPTY;
        }

        node.MetadataVersion++;
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
        var before = node.FileInfo;

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

        // Only a real change marks the disk dirty: a request that leaves every field as it was
        // (nothing requested, or the current values re-asserted) must not trigger an auto-save.
        if (node.FileInfo.FileAttributes != before.FileAttributes ||
            node.FileInfo.CreationTime != before.CreationTime ||
            node.FileInfo.LastAccessTime != before.LastAccessTime ||
            node.FileInfo.LastWriteTime != before.LastWriteTime ||
            node.FileInfo.ChangeTime != before.ChangeTime)
        {
            node.MetadataVersion++;
            MarkDirty();
        }

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
    /// STATUS_SUCCESS, STATUS_DISK_FULL, or STATUS_INSUFFICIENT_RESOURCES.
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
        var versionBefore = node.ContentVersion;
        var result = SetFileSizeCore(node, newSize, setAllocationSize);

        // SetFileSizeCore bumps ContentVersion exactly when it changes something; a no-op resize
        // (common: callers re-assert the current size) must not trigger an auto-save.
        if (node.ContentVersion != versionBefore)
        {
            MarkDirty();
        }

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
        node.MetadataVersion++;
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
    /// STATUS_SUCCESS, STATUS_DISK_FULL, or STATUS_INSUFFICIENT_RESOURCES.
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

        if (writeOffset > ulong.MaxValue - length)
        {
            // An offset this close to ulong.MaxValue can never fit within any real capacity;
            // reject it before the addition below wraps around and is mistaken for a small,
            // already-covered write.
            return STATUS_DISK_FULL;
        }

        var writeEnd = writeOffset + length;
        var originalFileSize = node.FileInfo.FileSize;

        if (writeEnd > originalFileSize)
        {
            var result = SetFileSizeCore(node, writeEnd, setAllocationSize: false);
            if (result != STATUS_SUCCESS)
            {
                return result;
            }
        }

        if (length > 0 && node.FileData != null)
        {
            // Growth above only reserves sparse space; this is where memory is actually
            // allocated, so it's where the low-memory guard is charged. On denial, undo the size
            // extension so the failed write leaves the file's visible size as it was (the grown
            // allocation stays, but is sparse and costs nothing).
            if (!node.FileData.TryWriteFrom(buffer, writeOffset, length, _memoryBudget))
            {
                if (node.FileInfo.FileSize != originalFileSize)
                {
                    node.FileInfo.FileSize = originalFileSize;
                    MarkDirty();
                }

                fileInfo = node.FileInfo;
                return STATUS_INSUFFICIENT_RESOURCES;
            }

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
        RecordLastPath(ref _lastContentWritePath, node.FilePath);
        fileInfo = node.FileInfo;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Gets a mutation-version snapshot suitable for a later <see cref="ClearDirtySince"/> call.
    /// A save should capture this <em>before</em> it starts copying node data, so that any
    /// mutation racing the save is not lost when the save completes.
    /// </summary>
    internal long CaptureMutationVersion() => Interlocked.Read(ref _mutationVersion);

    /// <summary>
    /// Marks the disk's content as up to date with the on-disk image as of right now.
    /// Equivalent to <c>ClearDirtySince(CaptureMutationVersion())</c>; a plain convenience for
    /// callers (tests, or a save with no concurrent-mutation concern) that don't need to protect
    /// against a write racing an in-progress save.
    /// </summary>
    internal void ClearDirty() => ClearDirtySince(CaptureMutationVersion());

    /// <summary>
    /// Marks the disk as up to date with the on-disk image, but only if no mutation has
    /// happened since <paramref name="versionAtSaveStart"/> was captured (via
    /// <see cref="CaptureMutationVersion"/>) — otherwise a write that raced the save in
    /// progress would be silently forgotten, leaving the image stale with nothing left dirty
    /// to trigger a later save.
    /// </summary>
    /// <param name="versionAtSaveStart">The mutation version captured before the save began.</param>
    internal void ClearDirtySince(long versionAtSaveStart)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _savedVersion);
            if (current >= versionAtSaveStart)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _savedVersion, versionAtSaveStart, current) != current);
    }

    /// <summary>
    /// Marks the disk's content as changed since the last save.
    /// </summary>
    internal void MarkDirty() => MarkDirty(DateTimeOffset.UtcNow);

    /// <summary>
    /// Replaces this file system's entire contents with those of <paramref name="sourceMap"/>.
    /// Fails without modifying this file system when it is read-only, when the source's
    /// allocated bytes exceed this file system's capacity, or (when copying) when the copy's
    /// backing memory would trip the low-memory guard.
    /// </summary>
    /// <param name="sourceMap">The node map to take contents from.</param>
    /// <param name="error">Set to a human-readable message when the method returns <c>false</c>.</param>
    /// <param name="adoptNodes">
    /// <c>false</c> (cloning another mounted disk) deep-copies every node, since the source stays
    /// in use. <c>true</c> moves <paramref name="sourceMap"/>'s nodes in as-is, for a map nobody
    /// else holds (e.g. a snapshot just loaded to restore from): copying it would briefly double
    /// the memory its content already occupies, for a map about to be discarded anyway. The
    /// caller must not use <paramref name="sourceMap"/> afterwards.
    /// </param>
    /// <returns>
    /// <c>true</c> on success; <c>false</c> when the disk is read-only, too small, or the host is
    /// too low on memory for the copy.
    /// </returns>
    internal bool TryReplaceContents(FileNodeMap sourceMap, out string? error, bool adoptNodes = false)
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

        var nodes = sourceMap.GetAllNodes();

        if (!adoptNodes)
        {
            // The copy materializes every chunk the source has written, all at once, so it goes
            // through the same low-memory guard as writes do. The target's current contents are
            // freed only after this, so aren't credited against the copy.
            var copyBytes = 0L;
            foreach (var kvp in nodes)
            {
                copyBytes += kvp.Value.FileData?.CloneCost() ?? 0;
            }

            if (_memoryBudget.WouldExceed((ulong)copyBytes))
            {
                error = $"Not enough free memory to copy {copyBytes:N0} bytes of file content onto this disk.";
                return false;
            }
        }

        // Copies are made before taking the map's write lock, so a large clone doesn't stall every
        // file-system callback; the swap itself is then atomic.
        var replacement = adoptNodes
            ? nodes
            : nodes.Select(kvp => KeyValuePair.Create(kvp.Key, kvp.Value.Clone())).ToList();
        NodeMap.ReplaceAll(replacement);

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
    /// Skips the store when the path is unchanged so repeated I/O to the same file keeps the
    /// field's cache line shared across cores instead of bouncing it on every call.
    /// </summary>
    private static void RecordLastPath(ref string? field, string path)
    {
        if (!ReferenceEquals(Volatile.Read(ref field), path))
            Volatile.Write(ref field, path);
    }

    /// <summary>
    /// Marks the disk's content as changed since the last save, using a caller-supplied
    /// timestamp to avoid redundant <see cref="DateTimeOffset.UtcNow"/> calls on hot paths
    /// that already captured "now" for other purposes.
    /// </summary>
    private void MarkDirty(DateTimeOffset now)
    {
        Interlocked.Increment(ref _mutationVersion);
        Interlocked.Exchange(ref _lastContentWriteTicks, now.UtcTicks);
        ContentAccessed?.Invoke(true);
    }

    /// <summary>
    /// Core implementation for both file-size and allocation-size changes.
    /// When <paramref name="setAllocationSize"/> is <c>true</c>, resizes the backing buffer and
    /// clamps FileSize. When <c>false</c>, extends/truncates FileSize and grows allocation
    /// if needed.
    /// </summary>
    /// <returns>
    /// STATUS_SUCCESS, STATUS_DISK_FULL, or STATUS_INSUFFICIENT_RESOURCES.
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

            var previousAllocationSize = node.FileInfo.AllocationSize;

            // Checking headroom and applying the growth happen atomically (see
            // TryUpdateAllocationSizeWithinCapacity), so two concurrent extending writes on
            // different nodes can't both pass a stale capacity check and together push the real
            // total past _maxCapacity.
            if (!NodeMap.TryUpdateAllocationSizeWithinCapacity(node, aligned, _maxCapacity))
            {
                return STATUS_DISK_FULL;
            }

            if (aligned > 0)
            {
                if (node.FileData != null)
                {
                    // Growing is sparse and free, except for reallocating a tail chunk that already
                    // holds data; only that is charged against the low-memory guard.
                    if (!node.FileData.TryResize(aligned, _memoryBudget))
                    {
                        // The capacity reservation above already applied; undo it since the growth
                        // didn't actually happen.
                        NodeMap.UpdateAllocationSize(node, previousAllocationSize);
                        return STATUS_INSUFFICIENT_RESOURCES;
                    }
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
            else if (newSize < node.FileInfo.FileSize)
            {
                // The allocation (and the bytes in it) outlives a truncation, so clear the cut-off
                // tail now; otherwise a later extension within the same allocation would expose
                // the old data instead of zeros.
                node.FileData?.DiscardRange(newSize, node.FileInfo.FileSize - newSize);
            }

            node.FileInfo.FileSize = newSize;
        }

        node.ContentVersion++;
        return STATUS_SUCCESS;
    }
}
