namespace ManagedDrive.Core.FileSystem;

/// <summary>
/// Thread-safe, case-insensitive path-to-<see cref="FileNode"/> map that models the directory
/// tree of the in-memory file system. Keys are absolute paths using <c>\</c> as the separator
/// (e.g., <c>\Folder\File.txt</c>). The root directory is stored under the key <c>\</c>.
/// </summary>
public sealed class FileNodeMap : IDisposable
{
    private readonly Dictionary<string, FileNode> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parallel key index so directory enumeration (GetChildren) can seek directly to a path
    /// prefix's range in O(log n) via GetViewBetween, instead of scanning the whole namespace
    /// from the start looking for where the prefix run begins. _map itself is a plain Dictionary
    /// (O(1) lookup/insert/remove) precisely because it no longer needs to maintain order.
    /// </summary>
    private readonly SortedSet<string> _sortedKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Read/write lock instead of a mutual-exclusion lock: lookups and directory enumerations
    /// (the read-heavy majority) can proceed concurrently, and a full-scan enumeration no longer
    /// blocks unrelated metadata lookups. Structural mutations still take the exclusive write lock.
    /// </summary>
    private readonly ReaderWriterLockSlim _syncRoot = new(LockRecursionPolicy.NoRecursion);

    /// <summary>
    /// Whether any node in the map may be a symbolic link or junction. Lets
    /// <see cref="TryFindReparsePrefix"/>, which runs on every failed lookup (i.e. before every
    /// create), return immediately on a disk that holds none. Set whenever a link node is added
    /// (<see cref="AddCore"/>) or made (<see cref="NoteReparsePoint"/>) and never cleared again, so
    /// it can be <c>true</c> with no link left (the lookup then just does the walk), but never
    /// <c>false</c> while one exists. Not clearing it when the map is emptied is deliberate: the
    /// reader doesn't take the lock, so a reset inside <see cref="ReplaceAll"/> would let it read
    /// <c>false</c> after the old links are gone but before the new ones are added.
    /// </summary>
    private bool _mayHaveReparse;

    /// <summary>
    /// Running total of <see cref="Fsp.Interop.FileInfo.AllocationSize"/> across every stored
    /// node, in bytes. Signed (rather than <c>ulong</c>, which the public API still exposes via
    /// <see cref="GetTotalAllocated"/>) because it is updated exclusively via
    /// <see cref="Interlocked"/>, whose <c>long</c> overloads are the ones guaranteed available —
    /// this value never approaches <see cref="long.MaxValue"/> for any real disk capacity.
    /// <see cref="UpdateAllocationSize"/> (the hot path, called on every extending write) mutates
    /// it while holding only the *read* lock, so it can run concurrently with other calls to
    /// itself, but every mutation site below still uses <c>Interlocked</c> (rather than a plain
    /// <c>+=</c>/<c>-=</c> under the write lock they hold anyway) so their updates compose
    /// correctly with a concurrent <see cref="UpdateAllocationSize"/> call instead of racing it.
    /// </summary>
    private long _totalAllocated;

    /// <summary>
    /// Gets the number of nodes currently stored in the map.
    /// </summary>
    public int Count
    {
        get
        {
            _syncRoot.EnterReadLock();
            try
            {
                return _map.Count;
            }
            finally
            {
                _syncRoot.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Inserts or replaces the node at <paramref name="filePath"/> and updates
    /// <see cref="FileNode.FilePath"/> on the node to match.
    /// </summary>
    /// <param name="filePath">Absolute file-system path (e.g. <c>\Folder\File.txt</c>).</param>
    /// <param name="node">The file node to store.</param>
    public void Add(string filePath, FileNode node)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            AddCore(filePath, node);
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Checked counterpart to <see cref="Add"/> for creating a new node: under one write-lock
    /// acquisition, verifies that nothing already exists at <paramref name="filePath"/>, that its
    /// parent directory exists, and that <paramref name="node"/>'s allocation size would not push
    /// the running total past <paramref name="maxCapacity"/>, and only then adds it. Checking these
    /// separately from the add would let a concurrent create of the same name be silently
    /// replaced, a node be added under a directory being deleted (leaving it an unreachable
    /// orphan), or two creates both pass a stale capacity check — the same check-then-apply race
    /// <see cref="TryUpdateAllocationSizeWithinCapacity"/> closes for growing an existing node.
    /// </summary>
    /// <param name="filePath">Absolute file-system path of the new node.</param>
    /// <param name="node">The file node to store.</param>
    /// <param name="maxCapacity">
    /// Returns the volume's current capacity ceiling, in bytes. Called under the write lock rather
    /// than passed as a value, so a ceiling lowered in the meantime (see
    /// <see cref="TryRunIfTotalAllocatedWithin"/>) is honored instead of a stale one.
    /// </param>
    /// <returns><see cref="CreateResult.Created"/> if added; otherwise why not (nothing changed).</returns>
    public CreateResult TryCreate(string filePath, FileNode node, Func<ulong> maxCapacity)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            if (_map.ContainsKey(filePath))
            {
                return CreateResult.NameCollision;
            }

            switch (GetParentStateCore(filePath))
            {
                case ParentState.Missing:
                    return CreateResult.ParentNotFound;
                case ParentState.NotDirectory:
                    return CreateResult.ParentNotDirectory;
            }

            // A stream can only exist next to the file it belongs to; checked here, under the same
            // lock as the add, so it can't be created for an owner that is being deleted.
            if (AlternateStreamName.IsStreamKey(filePath) &&
                !_map.ContainsKey(AlternateStreamName.OwnerOf(filePath)))
            {
                return CreateResult.OwnerNotFound;
            }

            if ((ulong)Interlocked.Read(ref _totalAllocated) + node.FileInfo.AllocationSize > maxCapacity())
            {
                return CreateResult.CapacityExceeded;
            }

            AddCore(filePath, node);
            return CreateResult.Created;
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Outcome of <see cref="TryCreate"/>, mirroring the cases <c>MemoryFileSystem.Create</c> must
    /// translate into distinct NTSTATUS codes.
    /// </summary>
    public enum CreateResult
    {
        /// <summary>
        /// The node was added.
        /// </summary>
        Created,

        /// <summary>
        /// A node already exists at the path.
        /// </summary>
        NameCollision,

        /// <summary>
        /// The parent directory doesn't exist (e.g. it was just deleted).
        /// </summary>
        ParentNotFound,

        /// <summary>
        /// The parent path names a file, not a directory.
        /// </summary>
        ParentNotDirectory,

        /// <summary>
        /// Adding the node would exceed the volume's capacity.
        /// </summary>
        CapacityExceeded,

        /// <summary>
        /// The node is an alternate data stream whose file doesn't exist.
        /// </summary>
        OwnerNotFound,
    }

    /// <summary>
    /// Whether the directory that would contain a given path exists, as reported by
    /// <see cref="GetParentStateCore"/>.
    /// </summary>
    private enum ParentState
    {
        /// <summary>
        /// The parent exists and is a directory.
        /// </summary>
        Directory,

        /// <summary>
        /// Nothing exists at the parent path.
        /// </summary>
        Missing,

        /// <summary>
        /// The parent path names a file.
        /// </summary>
        NotDirectory,
    }

    /// <summary>
    /// Looks up the directory that would contain <paramref name="filePath"/>. The volume root always
    /// counts as present: it is created when the file system initializes, before any path beneath it
    /// can be created, and is never removed. Caller must hold the read (or write) lock.
    /// </summary>
    /// <param name="filePath">Absolute file-system path whose parent to check.</param>
    /// <returns>The parent's state.</returns>
    private ParentState GetParentStateCore(string filePath)
    {
        var lastSeparator = filePath.LastIndexOf('\\');
        if (lastSeparator <= 0)
        {
            return ParentState.Directory;
        }

        if (!_map.TryGetValue(filePath[..lastSeparator], out var parent))
        {
            return ParentState.Missing;
        }

        return parent.IsDirectory ? ParentState.Directory : ParentState.NotDirectory;
    }

    /// <summary>
    /// Lock-free core of <see cref="Add"/> and <see cref="TryCreate"/>: inserts or
    /// replaces the node at <paramref name="filePath"/>, updates <see cref="_sortedKeys"/>, and
    /// adjusts <see cref="_totalAllocated"/>. Also reserves the node's index number (see
    /// <see cref="FileNode.ReserveIndexNumber"/>) — every loaded image, snapshot, and archive
    /// node passes through here. Caller must already hold the write lock.
    /// </summary>
    /// <param name="filePath">Absolute file-system path (e.g. <c>\Folder\File.txt</c>).</param>
    /// <param name="node">The file node to store.</param>
    private void AddCore(string filePath, FileNode node)
    {
        FileNode.ReserveIndexNumber(node.FileInfo.IndexNumber);

        if (_map.TryGetValue(filePath, out var existing))
        {
            Interlocked.Add(ref _totalAllocated, -(long)existing.FileInfo.AllocationSize);
            existing.IsDetached = true;
        }
        else
        {
            _sortedKeys.Add(filePath);
        }

        if (node.ReparseData is not null)
        {
            Volatile.Write(ref _mayHaveReparse, true);
        }

        node.FilePath = filePath;
        node.LeafName = ComputeLeafName(filePath);
        node.IsDetached = false;
        _map[filePath] = node;
        Interlocked.Add(ref _totalAllocated, (long)node.FileInfo.AllocationSize);
    }

    /// <summary>
    /// Removes all nodes except the root directory entry (<c>\</c>).
    /// </summary>
    public void ClearAll()
    {
        _syncRoot.EnterWriteLock();
        try
        {
            ClearAllCore();
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Replaces the map's entire contents with <paramref name="nodes"/> under a single write-lock
    /// acquisition, so a concurrent file-system callback never observes the half-emptied map that a
    /// separate <see cref="ClearAll"/> followed by one <see cref="Add"/> per node would expose. The
    /// current root is kept unless <paramref name="nodes"/> supplies its own.
    /// </summary>
    /// <param name="nodes">The path/node pairs to store; the nodes must not belong to another live map.</param>
    public void ReplaceAll(IEnumerable<KeyValuePair<string, FileNode>> nodes)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            ClearAllCore();
            foreach (var kvp in nodes)
            {
                AddCore(kvp.Key, kvp.Value);
            }
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Lock-free core of <see cref="ClearAll"/> and <see cref="ReplaceAll"/>: removes every node
    /// except the root, marking each removed node <see cref="FileNode.IsDetached"/> so a handle
    /// still holding one can't skew <see cref="_totalAllocated"/>. Caller must already hold the
    /// write lock.
    /// </summary>
    private void ClearAllCore()
    {
        var hasRoot = _map.TryGetValue("\\", out var root);
        foreach (var node in _map.Values)
        {
            node.IsDetached = true;
        }

        _map.Clear();
        _sortedKeys.Clear();
        Interlocked.Exchange(ref _totalAllocated, 0);

        if (hasRoot)
        {
            root!.IsDetached = false;
            _map["\\"] = root;
            _sortedKeys.Add("\\");
            Interlocked.Exchange(ref _totalAllocated, (long)root.FileInfo.AllocationSize);
        }
    }

    /// <summary>
    /// Releases the reader/writer lock backing this map. Call only once the owning file system is
    /// no longer serving callbacks.
    /// </summary>
    public void Dispose()
    {
        _syncRoot.Dispose();
    }

    /// <summary>
    /// Returns a snapshot of all nodes in the map, in sorted path order.
    /// </summary>
    /// <returns>
    /// A sequence of all (path, node) pairs currently stored in the map.
    /// </returns>
    public IReadOnlyList<KeyValuePair<string, FileNode>> GetAllNodes() => GetAllNodes(out _);

    /// <summary>
    /// Returns a snapshot of all nodes in the map, in sorted path order, together with each
    /// node's <see cref="FileNode.MetadataVersion"/> as of the same instant. A rename changes a
    /// node's path and bumps its version under one write-lock acquisition, so each captured
    /// version is exactly the one that goes with the captured path — reading the versions later
    /// instead could pair a pre-rename path with a post-rename version.
    /// </summary>
    /// <param name="metadataVersions">
    /// Receives each node's metadata version, at the same index as the node in the result.
    /// </param>
    /// <returns>
    /// A sequence of all (path, node) pairs currently stored in the map.
    /// </returns>
    internal IReadOnlyList<KeyValuePair<string, FileNode>> GetAllNodes(out ulong[] metadataVersions)
    {
        _syncRoot.EnterReadLock();
        try
        {
            var result = new List<KeyValuePair<string, FileNode>>(_map.Count);
            metadataVersions = new ulong[_map.Count];
            foreach (var key in _sortedKeys)
            {
                var node = _map[key];
                metadataVersions[result.Count] = node.MetadataVersion;
                result.Add(new(key, node));
            }

            return result;
        }
        finally
        {
            _syncRoot.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns a snapshot of the immediate children of the directory at <paramref name="dirPath"/>,
    /// ordered by path. Entries whose name component is &lt;= <paramref name="marker"/> are
    /// skipped to support paged directory reads.
    /// </summary>
    /// <param name="dirPath">Absolute path of the directory to enumerate.</param>
    /// <param name="marker">
    /// When non-<c>null</c>, child entries whose name is &lt;= this value are skipped.
    /// </param>
    /// <returns>
    /// A sequence of (path, node) pairs for immediate children of <paramref name="dirPath"/>.
    /// </returns>
    public IReadOnlyList<KeyValuePair<string, FileNode>> GetChildren(string dirPath, string? marker)
    {
        List<KeyValuePair<string, FileNode>> matches = [];
        _syncRoot.EnterReadLock();
        try
        {
            ScanImmediateChildren(dirPath, marker, matches);
        }
        finally
        {
            _syncRoot.ExitReadLock();
        }

        return matches;
    }

    /// <summary>
    /// Returns whether the directory at <paramref name="dirPath"/> has at least one immediate
    /// child. Equivalent to <c>GetChildren(dirPath, null).Any()</c> but never materializes the
    /// full child list — it returns as soon as the first match is found.
    /// </summary>
    /// <param name="dirPath">Absolute path of the directory to check.</param>
    public bool HasChildren(string dirPath)
    {
        _syncRoot.EnterReadLock();
        try
        {
            return ScanImmediateChildren(dirPath, marker: null, matches: null);
        }
        finally
        {
            _syncRoot.ExitReadLock();
        }
    }

    /// <summary>
    /// Walks the immediate children of <paramref name="dirPath"/> in <see cref="_sortedKeys"/>
    /// order, jumping over each child directory's subtree rather than stepping through it, so the
    /// cost scales with the number of immediate children (times O(log n) per subtree jump) instead
    /// of the size of the whole subtree below <paramref name="dirPath"/>. Caller must hold the
    /// read (or write) lock.
    /// </summary>
    /// <param name="dirPath">Absolute path of the directory to enumerate.</param>
    /// <param name="marker">When non-<c>null</c>, children whose name is &lt;= this value are skipped.</param>
    /// <param name="matches">
    /// Receives every matching child; when <c>null</c>, the scan stops at the first match.
    /// </param>
    /// <returns><c>true</c> if at least one matching child was found.</returns>
    private bool ScanImmediateChildren(string dirPath, string? marker, List<KeyValuePair<string, FileNode>>? matches)
    {
        // For root "\" (length 1) the prefix equals dirPath itself; for others append "\"
        var prefix = dirPath.Length == 1 ? dirPath : (dirPath + "\\");

        // All keys sharing this prefix form a contiguous run in _sortedKeys (OrdinalIgnoreCase
        // order); the upper bound uses '￿', greater than any character in a real path. With a
        // marker, the scan seeks straight to it instead of walking every earlier child.
        var upperBound = prefix + '￿';
        var lowerBound = marker == null ? prefix : prefix + marker;
        var comparer = StringComparer.OrdinalIgnoreCase;
        var found = false;

        while (comparer.Compare(lowerBound, upperBound) <= 0)
        {
            string? resumeAt = null;
            foreach (var path in _sortedKeys.GetViewBetween(lowerBound, upperBound))
            {
                if (string.Equals(path, dirPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A separator after the prefix means this key is inside some child directory's
                // subtree. Keys starting with "prefix\child\" are contiguous and all sort before
                // "prefix\child]" (']' is the character right after '\'), so resume there.
                var childSpan = path.AsSpan(prefix.Length);
                var separator = childSpan.IndexOf('\\');
                if (separator >= 0)
                {
                    resumeAt = string.Concat(path.AsSpan(0, prefix.Length + separator), "]");
                    break;
                }

                // An alternate data stream is part of its file, not a sibling of it. A node whose
                // name has a colon without being a stream name (kept from an older build's image)
                // is hidden from listings too, but still counts as a child when only asking
                // whether the directory is empty, so the directory can't be deleted out from
                // under it.
                if (childSpan.Contains(AlternateStreamName.Separator))
                {
                    if (matches == null && !AlternateStreamName.IsWellFormedStreamKey(path))
                    {
                        return true;
                    }

                    continue;
                }

                if (marker != null &&
                    childSpan.CompareTo(marker, StringComparison.OrdinalIgnoreCase) <= 0)
                {
                    continue;
                }

                found = true;
                if (matches == null)
                {
                    return true;
                }

                matches.Add(new(path, _map[path]));
            }

            if (resumeAt == null)
            {
                break;
            }

            lowerBound = resumeAt;
        }

        return found;
    }

    /// <summary>
    /// Returns the total number of bytes currently allocated across all nodes in the map.
    /// </summary>
    /// <returns>
    /// The sum of <see cref="Fsp.Interop.FileInfo.AllocationSize"/> for every stored node.
    /// </returns>
    public ulong GetTotalAllocated() => (ulong)Interlocked.Read(ref _totalAllocated);

    /// <summary>
    /// Runs <paramref name="apply"/> only if the total allocation does not exceed
    /// <paramref name="limit"/>, with the check and <paramref name="apply"/> atomic with respect
    /// to every capacity-checked growth (<see cref="TryCreate"/>,
    /// <see cref="TryUpdateAllocationSizeWithinCapacity"/>), which hold the read lock across their
    /// own check-and-apply. Used to lower the capacity ceiling without an in-flight growth that
    /// passed against the old ceiling landing after the check.
    /// </summary>
    /// <param name="limit">The largest total allocation, in bytes, for which <paramref name="apply"/> may run.</param>
    /// <param name="apply">The action to run when the total is within <paramref name="limit"/>.</param>
    /// <returns><c>true</c> if <paramref name="apply"/> ran; <c>false</c> if the total exceeds <paramref name="limit"/>.</returns>
    public bool TryRunIfTotalAllocatedWithin(ulong limit, Action apply)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            if (GetTotalAllocated() > limit)
            {
                return false;
            }

            apply();
            return true;
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes the node at <paramref name="filePath"/>, if present.
    /// </summary>
    /// <param name="filePath">Absolute file-system path.</param>
    public void Remove(string filePath)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            RemoveCore(filePath);
            RemoveStreamsCore(filePath);
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Deletes the node at <paramref name="filePath"/> through an open handle to
    /// <paramref name="expected"/>, only if it is still that node and — for a directory — still
    /// empty. If the handle's node was already swapped out (e.g. by a format or snapshot restore)
    /// and a different node now lives at the same path, a plain <see cref="Remove"/> would delete
    /// that unrelated node instead. And a directory can gain a child between the emptiness check
    /// that allowed its deletion and this call; removing it anyway would leave that child an
    /// unreachable orphan, so it is kept instead.
    /// </summary>
    /// <param name="filePath">Absolute file-system path.</param>
    /// <param name="expected">The node the caller means to delete.</param>
    /// <returns>
    /// <c>true</c> if <paramref name="expected"/> was stored at <paramref name="filePath"/> and was removed.
    /// </returns>
    public bool TryDelete(string filePath, FileNode expected)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            if (!_map.TryGetValue(filePath, out var current) || !ReferenceEquals(current, expected))
            {
                return false;
            }

            if (expected.IsDirectory && ScanImmediateChildren(filePath, marker: null, matches: null))
            {
                return false;
            }

            RemoveCore(filePath);
            RemoveStreamsCore(filePath);
            return true;
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Deletes the empty file at <paramref name="filePath"/> only if it is still
    /// <paramref name="expected"/>, still has no content and has no alternate data streams, all
    /// checked under the write lock. Used to undo the implicit creation of a stream's file when
    /// the stream itself could not be created, without taking along a stream or data another
    /// thread attached to that file in the meantime.
    /// </summary>
    /// <param name="filePath">Absolute path of the file.</param>
    /// <param name="expected">The node the caller created and now wants to take back.</param>
    /// <returns><c>true</c> if the file was removed.</returns>
    public bool TryDeleteIfUnused(string filePath, FileNode expected)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            if (!_map.TryGetValue(filePath, out var current) ||
                !ReferenceEquals(current, expected) ||
                expected.FileInfo.FileSize != 0 ||
                GetStreamKeysCore(filePath).Count != 0)
            {
                return false;
            }

            RemoveCore(filePath);
            return true;
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Lock-free core of <see cref="Remove"/> and <see cref="Rename"/>: removes the node at
    /// <paramref name="filePath"/>, if present, updating <see cref="_sortedKeys"/> and
    /// <see cref="_totalAllocated"/> to match. Caller must already hold the write lock.
    /// </summary>
    /// <param name="filePath">Absolute file-system path.</param>
    private void RemoveCore(string filePath)
    {
        if (_map.Remove(filePath, out var removed))
        {
            _sortedKeys.Remove(filePath);
            Interlocked.Add(ref _totalAllocated, -(long)removed.FileInfo.AllocationSize);
            removed.IsDetached = true;
        }
    }

    /// <summary>
    /// Removes the node at <paramref name="dirPath"/> together with every descendant beneath it
    /// (anything whose path starts with <c>dirPath + "\\"</c>). Used when a rename replaces an
    /// existing directory so its former children don't linger as orphans in the map.
    /// </summary>
    /// <param name="dirPath">Absolute path of the directory (or file) to remove, with its subtree.</param>
    public void RemoveSubtree(string dirPath)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            RemoveSubtreeCore(dirPath);
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Lock-free core of <see cref="RemoveSubtree"/> and <see cref="Rename"/>: removes the node at
    /// <paramref name="dirPath"/> together with every descendant beneath it, updating
    /// <see cref="_sortedKeys"/> and <see cref="_totalAllocated"/> to match. Caller must already
    /// hold the write lock.
    /// </summary>
    /// <param name="dirPath">Absolute path of the directory (or file) to remove, with its subtree.</param>
    private void RemoveSubtreeCore(string dirPath)
    {
        if (_map.Remove(dirPath, out var removed))
        {
            _sortedKeys.Remove(dirPath);
            Interlocked.Add(ref _totalAllocated, -(long)removed.FileInfo.AllocationSize);
            removed.IsDetached = true;
        }

        RemoveStreamsCore(dirPath);

        var prefix = dirPath + "\\";
        var upperBound = prefix + '￿';
        var keys = new List<string>(_sortedKeys.GetViewBetween(prefix, upperBound));

        foreach (var key in keys)
        {
            if (_map.Remove(key, out var descendant))
            {
                _sortedKeys.Remove(key);
                Interlocked.Add(ref _totalAllocated, -(long)descendant.FileInfo.AllocationSize);
                descendant.IsDetached = true;
            }
        }
    }

    /// <summary>
    /// Lists the keys of the alternate data streams of <paramref name="ownerPath"/>, in sorted
    /// order. Caller must hold the read (or write) lock.
    /// </summary>
    /// <param name="ownerPath">Absolute path of the file or directory the streams belong to.</param>
    private List<string> GetStreamKeysCore(string ownerPath)
    {
        var prefix = ownerPath + AlternateStreamName.Separator;
        return [.. _sortedKeys.GetViewBetween(prefix, prefix + '￿')];
    }

    /// <summary>
    /// Removes every alternate data stream of <paramref name="ownerPath"/>, updating
    /// <see cref="_sortedKeys"/> and <see cref="_totalAllocated"/> to match. Caller must hold the
    /// write lock.
    /// </summary>
    /// <param name="ownerPath">Absolute path of the file or directory being removed.</param>
    private void RemoveStreamsCore(string ownerPath)
    {
        foreach (var key in GetStreamKeysCore(ownerPath))
        {
            if (_map.Remove(key, out var stream))
            {
                _sortedKeys.Remove(key);
                Interlocked.Add(ref _totalAllocated, -(long)stream.FileInfo.AllocationSize);
                stream.IsDetached = true;
            }
        }
    }

    /// <summary>
    /// Re-keys every alternate data stream of <paramref name="oldPath"/> so it belongs to
    /// <paramref name="newPath"/> instead. Caller must hold the write lock.
    /// </summary>
    /// <param name="oldPath">Current absolute path of the file or directory being renamed.</param>
    /// <param name="newPath">New absolute path for it.</param>
    private void RenameStreamsCore(string oldPath, string newPath)
    {
        foreach (var key in GetStreamKeysCore(oldPath))
        {
            var stream = _map[key];
            _map.Remove(key);
            _sortedKeys.Remove(key);
            var newKey = string.Concat(newPath, key.AsSpan(oldPath.Length));
            stream.FilePath = newKey;
            stream.LeafName = ComputeLeafName(newKey);
            stream.MetadataVersion++;
            _map[newKey] = stream;
            _sortedKeys.Add(newKey);
        }
    }

    /// <summary>
    /// Returns the alternate data streams of the file or directory at <paramref name="ownerPath"/>,
    /// ordered by name.
    /// </summary>
    /// <param name="ownerPath">Absolute path of the file or directory.</param>
    /// <returns>The (path, node) pairs of its streams; empty when it has none.</returns>
    public IReadOnlyList<KeyValuePair<string, FileNode>> GetStreams(string ownerPath)
    {
        _syncRoot.EnterReadLock();
        try
        {
            return [.. GetStreamKeysCore(ownerPath).Select(key => KeyValuePair.Create(key, _map[key]))];
        }
        finally
        {
            _syncRoot.ExitReadLock();
        }
    }

    /// <summary>
    /// Removes every well-formed stream node (key in the normalized form of
    /// <see cref="AlternateStreamName.TryNormalize"/>) that <see cref="MemoryFileSystem"/> could not
    /// have created: one whose file is missing, or that is a directory. Run on every node map
    /// loaded from an image or snapshot, so a stream can never outlive or precede its file.
    /// A node whose name merely contains a colon without being a stream name (e.g. imported from
    /// an archive by an older build) is kept: it can't be reached through Windows, but dropping it
    /// would silently lose data on the next save.
    /// </summary>
    /// <returns>The keys of the nodes that were removed.</returns>
    internal IReadOnlyList<string> RemoveInvalidStreams()
    {
        _syncRoot.EnterWriteLock();
        try
        {
            List<string> removed = [];
            foreach (var key in _sortedKeys.Where(AlternateStreamName.IsStreamKey).ToList())
            {
                if (!AlternateStreamName.IsWellFormedStreamKey(key))
                {
                    continue;
                }

                if (!_map.ContainsKey(AlternateStreamName.OwnerOf(key)) || _map[key].IsDirectory)
                {
                    RemoveCore(key);
                    removed.Add(key);
                }
            }

            return removed;
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Renames all descendant nodes of <paramref name="oldPath"/> so that their paths
    /// begin with <paramref name="newPath"/> instead.
    /// </summary>
    /// <param name="oldPath">Current absolute path of the directory being renamed.</param>
    /// <param name="newPath">New absolute path for the directory.</param>
    public void RenameDescendants(string oldPath, string newPath)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            RenameDescendantsCore(oldPath, newPath);
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Lock-free core of <see cref="RenameDescendants"/> and <see cref="Rename"/>: renames all
    /// descendant nodes of <paramref name="oldPath"/> so that their paths begin with
    /// <paramref name="newPath"/> instead. Caller must already hold the write lock.
    /// </summary>
    /// <param name="oldPath">Current absolute path of the directory being renamed.</param>
    /// <param name="newPath">New absolute path for the directory.</param>
    private void RenameDescendantsCore(string oldPath, string newPath)
    {
        var prefix = oldPath + "\\";
        var upperBound = prefix + '￿';
        var keys = new List<string>(_sortedKeys.GetViewBetween(prefix, upperBound));

        foreach (var key in keys)
        {
            var descendant = _map[key];
            _map.Remove(key);
            _sortedKeys.Remove(key);
            var newKey = string.Concat(newPath, key.AsSpan(oldPath.Length));
            descendant.FilePath = newKey;
            descendant.LeafName = ComputeLeafName(newKey);
            descendant.MetadataVersion++;
            _map[newKey] = descendant;
            _sortedKeys.Add(newKey);
        }
    }

    /// <summary>
    /// Outcome of <see cref="Rename"/>, mirroring the collision cases <c>MemoryFileSystem.Rename</c>
    /// must translate into distinct NTSTATUS codes.
    /// </summary>
    public enum RenameConflict
    {
        /// <summary>
        /// The rename was applied; no conflict.
        /// </summary>
        None,

        /// <summary>
        /// A node already exists at the destination path and <c>replaceIfExists</c> was <c>false</c>.
        /// </summary>
        NameCollision,

        /// <summary>
        /// The destination is a non-empty directory, so it can't be replaced.
        /// </summary>
        DirectoryNotEmpty,

        /// <summary>
        /// The destination is a directory but the node being renamed is a file.
        /// </summary>
        TargetIsDirectory,

        /// <summary>
        /// The destination is an empty directory of the same kind as the node being renamed.
        /// <c>replaceIfExists</c> never applies to a directory target on Windows, even an empty
        /// one, regardless of its value — only a file target can be replaced by a rename.
        /// </summary>
        CannotReplaceDirectory,

        /// <summary>
        /// The destination is a file but the node being renamed is a directory.
        /// </summary>
        TargetIsFile,

        /// <summary>
        /// The node is no longer the one at the source path (e.g. a handle opened before the disk
        /// was formatted or restored), so renaming it would clobber whatever lives there now.
        /// </summary>
        SourceNotFound,

        /// <summary>
        /// The destination's parent directory doesn't exist (e.g. it was just deleted).
        /// </summary>
        ParentNotFound,

        /// <summary>
        /// The destination's parent path names a file, not a directory.
        /// </summary>
        ParentNotDirectory,
    }

    /// <summary>
    /// Atomically renames <paramref name="node"/> (currently at <paramref name="fileName"/>) to
    /// <paramref name="newFileName"/> — checking for and clearing a colliding target, renaming a
    /// directory's descendants, and moving the node itself — under one write-lock acquisition.
    /// Doing this as separate <see cref="TryGet"/>/<see cref="HasChildren"/>/<see cref="RemoveSubtree"/>/
    /// <see cref="Remove"/>/<see cref="Add"/> calls (each its own lock acquisition) would leave
    /// windows where a concurrent structural mutation on another WinFsp driver thread — e.g. a
    /// <see cref="Add"/> landing between a "target has no children" check and
    /// <see cref="RemoveSubtree"/> unconditionally deleting that prefix — could be silently
    /// destroyed, or where the node is briefly absent from the map between removing it from
    /// <paramref name="fileName"/> and adding it at <paramref name="newFileName"/>.
    /// </summary>
    /// <param name="fileName">The node's current absolute path.</param>
    /// <param name="newFileName">The node's new absolute path.</param>
    /// <param name="node">The node being renamed (already retrieved by the caller).</param>
    /// <param name="replaceIfExists">Whether an existing node at <paramref name="newFileName"/> may be replaced.</param>
    /// <returns>
    /// <see cref="RenameConflict.None"/> on success (the rename was applied); otherwise the reason
    /// it was rejected, with no change made to the map.
    /// </returns>
    public RenameConflict Rename(string fileName, string newFileName, FileNode node, bool replaceIfExists)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            if (!_map.TryGetValue(fileName, out var current) || !ReferenceEquals(current, node))
            {
                return RenameConflict.SourceNotFound;
            }

            switch (GetParentStateCore(newFileName))
            {
                case ParentState.Missing:
                    return RenameConflict.ParentNotFound;
                case ParentState.NotDirectory:
                    return RenameConflict.ParentNotDirectory;
            }

            if (_map.TryGetValue(newFileName, out var existing) && !ReferenceEquals(existing, node))
            {
                if (!replaceIfExists)
                {
                    return RenameConflict.NameCollision;
                }

                if (existing.IsDirectory != node.IsDirectory)
                {
                    return existing.IsDirectory ? RenameConflict.TargetIsDirectory : RenameConflict.TargetIsFile;
                }

                if (existing.IsDirectory)
                {
                    if (ScanImmediateChildren(newFileName, marker: null, matches: null))
                    {
                        return RenameConflict.DirectoryNotEmpty;
                    }

                    return RenameConflict.CannotReplaceDirectory;
                }

                RemoveSubtreeCore(newFileName);
            }

            if (node.IsDirectory)
            {
                RenameDescendantsCore(fileName, newFileName);
            }

            RenameStreamsCore(fileName, newFileName);
            RemoveCore(fileName);
            AddCore(newFileName, node);

            // Bumped under the write lock, like the descendants' versions above, so a save that
            // snapshots the map after this rename can never see the new path alongside the old
            // version and reuse a segment still holding the node under its old path.
            node.MetadataVersion++;
            return RenameConflict.None;
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Attempts to retrieve the node at <paramref name="filePath"/>.
    /// </summary>
    /// <param name="filePath">Absolute file-system path.</param>
    /// <param name="node">The node if found; otherwise <c>null</c>.</param>
    /// <returns>
    /// <c>true</c> if the node was found; <c>false</c> otherwise.
    /// </returns>
    public bool TryGet(string filePath, out FileNode? node)
    {
        _syncRoot.EnterReadLock();
        try
        {
            return _map.TryGetValue(filePath, out node);
        }
        finally
        {
            _syncRoot.ExitReadLock();
        }
    }

    /// <summary>
    /// Outcome of <see cref="TryGetForLookup"/>, mirroring the cases a WinFsp callback that
    /// resolves a path by name (as opposed to an already-open handle, whose parent is known to
    /// exist) must translate into distinct NTSTATUS codes.
    /// </summary>
    public enum LookupResult
    {
        /// <summary>
        /// The node was found.
        /// </summary>
        Found,

        /// <summary>
        /// The parent exists and is a directory, but nothing exists at <c>filePath</c> itself.
        /// </summary>
        NotFound,

        /// <summary>
        /// Nothing exists at the parent path.
        /// </summary>
        ParentNotFound,

        /// <summary>
        /// The parent path names a file.
        /// </summary>
        ParentNotDirectory,
    }

    /// <summary>
    /// Looks up the node at <paramref name="filePath"/>, distinguishing a missing leaf (the
    /// parent exists and is a directory) from a missing or non-directory parent — the
    /// distinction between <c>STATUS_OBJECT_NAME_NOT_FOUND</c> and
    /// <c>STATUS_OBJECT_PATH_NOT_FOUND</c>/<c>STATUS_NOT_A_DIRECTORY</c> that a WinFsp callback
    /// resolving an arbitrary path (rather than a child of an already-open directory handle)
    /// must report.
    /// </summary>
    /// <param name="filePath">Absolute file-system path.</param>
    /// <param name="node">The node found, when this returns <see cref="LookupResult.Found"/>.</param>
    /// <returns>The outcome of the lookup.</returns>
    public LookupResult TryGetForLookup(string filePath, out FileNode? node)
    {
        _syncRoot.EnterReadLock();
        try
        {
            if (_map.TryGetValue(filePath, out node))
            {
                return LookupResult.Found;
            }

            return GetParentStateCore(filePath) switch
            {
                ParentState.Missing => LookupResult.ParentNotFound,
                ParentState.NotDirectory => LookupResult.ParentNotDirectory,
                _ => LookupResult.NotFound,
            };
        }
        finally
        {
            _syncRoot.ExitReadLock();
        }
    }

    /// <summary>
    /// Records that a node of this map is about to become a symbolic link or junction, so
    /// <see cref="TryFindReparsePrefix"/> stops short-circuiting. Must be called <em>before</em> the
    /// node's reparse data is set: a lookup that can see the link must also see this. Uses a full
    /// fence rather than a release store, which would not stop the caller's following plain write
    /// of the reparse data from becoming visible to another core first on a weakly ordered CPU.
    /// </summary>
    internal void NoteReparsePoint() => Interlocked.Exchange(ref _mayHaveReparse, true);

    /// <summary>
    /// Gets a value indicating whether <see cref="TryFindReparsePrefix"/> has any work to do; a
    /// <c>false</c> guarantees the map holds no symbolic link or junction.
    /// </summary>
    internal bool MayHaveReparsePoints => Volatile.Read(ref _mayHaveReparse);

    /// <summary>
    /// Finds the first symbolic link or junction among the directory components of
    /// <paramref name="filePath"/> (every component except the last one). A path that runs through
    /// such a node can never exist in the map, so this is what lets a failed lookup be reported as
    /// <c>STATUS_REPARSE</c> instead of "not found".
    /// </summary>
    /// <param name="filePath">Absolute file-system path.</param>
    /// <param name="reparsePointIndex">
    /// The index of the last character of the reparse point's own path, which is what WinFsp's
    /// native <c>FspFileSystemFindReparsePoint</c> reports (for <c>\a\b\c</c> with <c>\a\b</c> a
    /// reparse point, 3).
    /// </param>
    /// <returns><c>true</c> if a reparse point was found.</returns>
    public bool TryFindReparsePrefix(string filePath, out uint reparsePointIndex)
    {
        if (!Volatile.Read(ref _mayHaveReparse))
        {
            reparsePointIndex = 0;
            return false;
        }

        _syncRoot.EnterReadLock();
        try
        {
            for (var i = 1; i < filePath.Length; i++)
            {
                if (filePath[i] == '\\' &&
                    _map.TryGetValue(filePath[..i], out var prefixNode) &&
                    prefixNode.ReparseData is not null)
                {
                    reparsePointIndex = (uint)(i - 1);
                    return true;
                }
            }

            reparsePointIndex = 0;
            return false;
        }
        finally
        {
            _syncRoot.ExitReadLock();
        }
    }

    /// <summary>
    /// Updates <see cref="Fsp.Interop.FileInfo.AllocationSize"/> on <paramref name="node"/> and
    /// keeps the cached total returned by <see cref="GetTotalAllocated"/> in sync. This is the
    /// only supported way to change a node's allocation size outside of <see cref="Add"/> and
    /// <see cref="Remove"/>.
    /// </summary>
    /// <remarks>
    /// Takes the *read* lock rather than the write lock: this is the hot path for every extending
    /// write, and <see cref="FileNode.FileInfo"/>'s <c>AllocationSize</c> field is only ever
    /// mutated by the single WinFsp thread that owns <paramref name="node"/>, so it doesn't need
    /// exclusion against other calls to this method (which the read lock still allows to run
    /// concurrently on other nodes). It does need exclusion against <see cref="Add"/>,
    /// <see cref="Remove"/>, <see cref="RemoveSubtree"/>, and <see cref="ClearAll"/>, which read or
    /// overwrite the very node this call is resizing (or reset <see cref="_totalAllocated"/>
    /// outright) — the read lock, being mutually exclusive with their write lock, prevents this
    /// call's <see cref="Interlocked"/> update from racing with theirs or from being clobbered by
    /// <see cref="ClearAll"/>'s non-additive <see cref="Interlocked.Exchange(ref long, long)"/>.
    /// </remarks>
    /// <param name="node">The node whose allocation size is changing.</param>
    /// <param name="newAllocationSize">The new allocation size, in bytes.</param>
    public void UpdateAllocationSize(FileNode node, ulong newAllocationSize)
    {
        _syncRoot.EnterReadLock();
        try
        {
            var delta = (long)newAllocationSize - (long)node.FileInfo.AllocationSize;
            node.FileInfo.AllocationSize = newAllocationSize;

            // A node no longer in the map (still referenced by an open handle) isn't part of the
            // total, so its size changes must not be either.
            if (!node.IsDetached)
            {
                Interlocked.Add(ref _totalAllocated, delta);
            }
        }
        finally
        {
            _syncRoot.ExitReadLock();
        }
    }

    /// <summary>
    /// Capacity-checked counterpart to <see cref="UpdateAllocationSize"/>: when
    /// <paramref name="newAllocationSize"/> grows the node, atomically verifies that applying it
    /// would not push the running total past <paramref name="maxCapacityProvider"/> and only then applies
    /// it — via a compare-exchange loop on the same field <see cref="UpdateAllocationSize"/> uses,
    /// so this stays as lock-free as that hot path instead of escalating to the write lock. Without
    /// this, checking headroom (e.g. via <see cref="GetTotalAllocated"/>) and applying growth as two
    /// separate steps lets two concurrent extending writes on different nodes each see the same
    /// stale total, both pass the check, and together push the real total past
    /// <paramref name="maxCapacityProvider"/>.
    /// </summary>
    /// <param name="node">The node whose allocation size is changing.</param>
    /// <param name="newAllocationSize">The new allocation size, in bytes.</param>
    /// <param name="maxCapacityProvider">
    /// Returns the volume's current capacity ceiling, in bytes. Called while the read lock is held
    /// rather than passed as a value, so a ceiling lowered in the meantime (see
    /// <see cref="TryRunIfTotalAllocatedWithin"/>) is honored instead of a stale one.
    /// </param>
    /// <returns><c>true</c> if applied; <c>false</c> if it would have exceeded capacity (nothing changed).</returns>
    public bool TryUpdateAllocationSizeWithinCapacity(FileNode node, ulong newAllocationSize, Func<ulong> maxCapacityProvider)
    {
        if (newAllocationSize > maxCapacityProvider())
        {
            // Can never fit. Also keeps a size of 2^63 or more away from the signed delta below,
            // where it would turn negative and be applied as a "shrink" that bypasses the check.
            return false;
        }

        _syncRoot.EnterReadLock();
        try
        {
            // Read now that the read lock is held: a lowering of the ceiling takes the write lock,
            // so it either completed before this point (and is seen here) or waits for this call.
            var maxCapacity = maxCapacityProvider();
            var delta = (long)newAllocationSize - (long)node.FileInfo.AllocationSize;
            if (node.IsDetached)
            {
                // Not counted in the total (see UpdateAllocationSize). Shrinking is harmless, but
                // growth would take memory no capacity check accounts for, for a file nobody can
                // open again — refuse it.
                if (delta > 0)
                {
                    return false;
                }

                node.FileInfo.AllocationSize = newAllocationSize;
                return true;
            }

            if (delta <= 0)
            {
                node.FileInfo.AllocationSize = newAllocationSize;
                Interlocked.Add(ref _totalAllocated, delta);
                return true;
            }

            while (true)
            {
                var current = Interlocked.Read(ref _totalAllocated);
                if ((ulong)current + (ulong)delta > maxCapacity)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _totalAllocated, current + delta, current) == current)
                {
                    node.FileInfo.AllocationSize = newAllocationSize;
                    return true;
                }
            }
        }
        finally
        {
            _syncRoot.ExitReadLock();
        }
    }

    private static string ComputeLeafName(string filePath)
    {
        var lastSeparator = filePath.LastIndexOf('\\');
        return lastSeparator < 0 ? filePath : filePath[(lastSeparator + 1)..];
    }
}
