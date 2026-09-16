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
    /// Paths removed since the last successful image save, for incremental image save to emit tombstones for.
    /// Cleared by DrainRemovedSincePersist() once a save has picked them up.
    /// </summary>
    private readonly HashSet<string> _removedSincePersist = new(StringComparer.OrdinalIgnoreCase);

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
    /// this value never approaches <see cref="long.MaxValue"/> for any real disk capacity. It is
    /// deliberately not covered by <see cref="_syncRoot"/>: <see cref="UpdateAllocationSize"/>
    /// (the hot path, called on every extending write) mutates it without taking the write lock,
    /// so every other mutation site below must use <c>Interlocked</c> too, even though they
    /// already hold the write lock for their own structural changes — the write lock provides no
    /// exclusion against a thread that never takes it.
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
            _removedSincePersist.Remove(filePath);
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
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
            _removedSincePersist.Clear();

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
        // For root "\" (length 1) the prefix equals dirPath itself; for others append "\"
        var prefix = dirPath.Length == 1 ? dirPath : (dirPath + "\\");

        // All keys sharing this prefix form a contiguous run in _sortedKeys (OrdinalIgnoreCase
        // order). GetViewBetween seeks directly to that range in O(log n) instead of scanning
        // the whole namespace from the start looking for where the run begins — the upper bound
        // uses '￿', a value greater than any character used in a real path, so the view
        // covers exactly "prefix" plus everything that starts with it.
        var upperBound = prefix + '￿';

        List<KeyValuePair<string, FileNode>> matches = [];
        _syncRoot.EnterReadLock();
        try
        {
            foreach (var path in _sortedKeys.GetViewBetween(prefix, upperBound))
            {
                if (string.Equals(path, dirPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Only immediate children: no additional backslash after the prefix. Compare via
                // span so the common marker == null case never allocates a substring just to
                // test for a separator.
                var childSpan = path.AsSpan(prefix.Length);
                if (childSpan.Contains('\\'))
                {
                    continue;
                }

                if (marker != null &&
                    childSpan.CompareTo(marker, StringComparison.OrdinalIgnoreCase) <= 0)
                {
                    continue;
                }

                matches.Add(new(path, _map[path]));
            }
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
        var prefix = dirPath.Length == 1 ? dirPath : (dirPath + "\\");
        var upperBound = prefix + '￿';

        _syncRoot.EnterReadLock();
        try
        {
            foreach (var path in _sortedKeys.GetViewBetween(prefix, upperBound))
            {
                if (string.Equals(path, dirPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (path.AsSpan(prefix.Length).Contains('\\'))
                {
                    continue;
                }

                return true;
            }

            return false;
        }
        finally
        {
            _syncRoot.ExitReadLock();
        }
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
            if (_map.Remove(filePath, out var removed))
            {
                _sortedKeys.Remove(filePath);
                Interlocked.Add(ref _totalAllocated, -(long)removed.FileInfo.AllocationSize);
                _removedSincePersist.Add(filePath);
            }
        }
        finally
        {
            _syncRoot.ExitWriteLock();
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
            if (_map.Remove(dirPath, out var removed))
            {
                _sortedKeys.Remove(dirPath);
                Interlocked.Add(ref _totalAllocated, -(long)removed.FileInfo.AllocationSize);
                _removedSincePersist.Add(dirPath);
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
                    _removedSincePersist.Add(key);
                }
            }
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
                _removedSincePersist.Add(key);
                _removedSincePersist.Remove(newKey);
            }
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
    /// Deliberately does not take <see cref="_syncRoot"/>: this is the hot path for every
    /// extending write, and <see cref="FileNode.FileInfo"/>'s <c>AllocationSize</c> field is only
    /// ever mutated by the single WinFsp thread that owns <paramref name="node"/>, so no lock is
    /// needed to protect it. The shared <see cref="_totalAllocated"/> counter is still safe to
    /// update concurrently with structural changes elsewhere in the map because every mutation
    /// site uses <see cref="Interlocked"/>.
    /// </remarks>
    /// <param name="node">The node whose allocation size is changing.</param>
    /// <param name="newAllocationSize">The new allocation size, in bytes.</param>
    public void UpdateAllocationSize(FileNode node, ulong newAllocationSize)
    {
        var delta = (long)newAllocationSize - (long)node.FileInfo.AllocationSize;
        node.FileInfo.AllocationSize = newAllocationSize;
        Interlocked.Add(ref _totalAllocated, delta);
    }

    /// <summary>
    /// Returns the set of paths removed since the last call to this method, and clears it. Today's
    /// incremental image save (<c>DiskImageSerializer.SaveSegmentedIncremental</c>) detects removed
    /// nodes indirectly instead — a segment's member count no longer matching what was recorded for
    /// it is enough to force a rewrite — so it calls this purely to reset <see cref="_removedSincePersist"/>
    /// after each successful save and does not use the returned paths. They're returned (rather
    /// than this being a void <c>Reset()</c>) for a possible future save format that writes explicit
    /// per-path tombstones; if a save that would consume the result fails, the caller is responsible
    /// for not losing track of it.
    /// </summary>
    /// <returns>The paths removed since the previous drain.</returns>
    internal IReadOnlySet<string> DrainRemovedSincePersist()
    {
        _syncRoot.EnterWriteLock();
        try
        {
            if (_removedSincePersist.Count == 0)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var result = new HashSet<string>(_removedSincePersist, StringComparer.OrdinalIgnoreCase);
            _removedSincePersist.Clear();
            return result;
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
    }

    private static string ComputeLeafName(string filePath)
    {
        var lastSeparator = filePath.LastIndexOf('\\');
        return lastSeparator < 0 ? filePath : filePath[(lastSeparator + 1)..];
    }
}