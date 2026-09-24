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
    /// Capacity-checked counterpart to <see cref="Add"/>: atomically verifies that
    /// <paramref name="node"/>'s allocation size would not push the running total past
    /// <paramref name="maxCapacity"/> and only then adds it, under one write-lock acquisition —
    /// closing the same check-then-apply race <see cref="TryUpdateAllocationSizeWithinCapacity"/>
    /// closes for growing an existing node.
    /// </summary>
    /// <returns><c>true</c> if added; <c>false</c> if it would have exceeded capacity (nothing changed).</returns>
    public bool TryAddWithinCapacity(string filePath, FileNode node, ulong maxCapacity)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            if ((ulong)Interlocked.Read(ref _totalAllocated) + node.FileInfo.AllocationSize > maxCapacity)
            {
                return false;
            }

            AddCore(filePath, node);
            return true;
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    /// <summary>
    /// Lock-free core of <see cref="Add"/> and <see cref="TryAddWithinCapacity"/>: inserts or
    /// replaces the node at <paramref name="filePath"/>, updates <see cref="_sortedKeys"/>, and
    /// adjusts <see cref="_totalAllocated"/>. Caller must already hold the write lock.
    /// </summary>
    /// <param name="filePath">Absolute file-system path (e.g. <c>\Folder\File.txt</c>).</param>
    /// <param name="node">The file node to store.</param>
    private void AddCore(string filePath, FileNode node)
    {
        if (_map.TryGetValue(filePath, out var existing))
        {
            Interlocked.Add(ref _totalAllocated, -(long)existing.FileInfo.AllocationSize);
        }
        else
        {
            _sortedKeys.Add(filePath);
        }

        node.FilePath = filePath;
        node.LeafName = ComputeLeafName(filePath);
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
            var hasRoot = _map.TryGetValue("\\", out var root);
            _map.Clear();
            _sortedKeys.Clear();
            Interlocked.Exchange(ref _totalAllocated, 0);

            if (hasRoot)
            {
                _map["\\"] = root!;
                _sortedKeys.Add("\\");
                Interlocked.Exchange(ref _totalAllocated, (long)root!.FileInfo.AllocationSize);
            }
        }
        finally
        {
            _syncRoot.ExitWriteLock();
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
    public IReadOnlyList<KeyValuePair<string, FileNode>> GetAllNodes()
    {
        _syncRoot.EnterReadLock();
        try
        {
            var result = new List<KeyValuePair<string, FileNode>>(_map.Count);
            foreach (var key in _sortedKeys)
            {
                result.Add(new(key, _map[key]));
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
    /// Removes the node at <paramref name="filePath"/>, if present.
    /// </summary>
    /// <param name="filePath">Absolute file-system path.</param>
    public void Remove(string filePath)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            RemoveCore(filePath);
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
        }

        var prefix = dirPath + "\\";
        var upperBound = prefix + '￿';
        var keys = new List<string>(_sortedKeys.GetViewBetween(prefix, upperBound));

        foreach (var key in keys)
        {
            if (_map.Remove(key, out var descendant))
            {
                _sortedKeys.Remove(key);
                Interlocked.Add(ref _totalAllocated, -(long)descendant.FileInfo.AllocationSize);
            }
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
        /// The destination is a file but the node being renamed is a directory.
        /// </summary>
        TargetIsFile,
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

                if (existing.IsDirectory && ScanImmediateChildren(newFileName, marker: null, matches: null))
                {
                    return RenameConflict.DirectoryNotEmpty;
                }

                // Directories are already verified empty above, but RemoveSubtreeCore also clears
                // any descendants left behind by an earlier inconsistency instead of orphaning them.
                RemoveSubtreeCore(newFileName);
            }

            if (node.IsDirectory)
            {
                RenameDescendantsCore(fileName, newFileName);
            }

            RemoveCore(fileName);
            AddCore(newFileName, node);
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
            Interlocked.Add(ref _totalAllocated, delta);
        }
        finally
        {
            _syncRoot.ExitReadLock();
        }
    }

    /// <summary>
    /// Capacity-checked counterpart to <see cref="UpdateAllocationSize"/>: when
    /// <paramref name="newAllocationSize"/> grows the node, atomically verifies that applying it
    /// would not push the running total past <paramref name="maxCapacity"/> and only then applies
    /// it — via a compare-exchange loop on the same field <see cref="UpdateAllocationSize"/> uses,
    /// so this stays as lock-free as that hot path instead of escalating to the write lock. Without
    /// this, checking headroom (e.g. via <see cref="GetTotalAllocated"/>) and applying growth as two
    /// separate steps lets two concurrent extending writes on different nodes each see the same
    /// stale total, both pass the check, and together push the real total past
    /// <paramref name="maxCapacity"/>.
    /// </summary>
    /// <param name="node">The node whose allocation size is changing.</param>
    /// <param name="newAllocationSize">The new allocation size, in bytes.</param>
    /// <param name="maxCapacity">The volume's capacity ceiling, in bytes.</param>
    /// <returns><c>true</c> if applied; <c>false</c> if it would have exceeded capacity (nothing changed).</returns>
    public bool TryUpdateAllocationSizeWithinCapacity(FileNode node, ulong newAllocationSize, ulong maxCapacity)
    {
        _syncRoot.EnterReadLock();
        try
        {
            var delta = (long)newAllocationSize - (long)node.FileInfo.AllocationSize;
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
