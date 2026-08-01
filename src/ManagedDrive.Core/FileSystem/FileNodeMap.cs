namespace ManagedDrive.Core.FileSystem;

/// <summary>
/// Thread-safe, case-insensitive path-to-<see cref="FileNode"/> map that models the directory
/// tree of the in-memory file system. Keys are absolute paths using <c>\</c> as the separator
/// (e.g., <c>\Folder\File.txt</c>). The root directory is stored under the key <c>\</c>.
/// </summary>
public sealed class FileNodeMap : IDisposable
{
    private readonly Dictionary<string, FileNode> _map = new(StringComparer.OrdinalIgnoreCase);

    // Parallel key index so directory enumeration (GetChildren) can seek directly to a path
    // prefix's range in O(log n) via GetViewBetween, instead of scanning the whole namespace
    // from the start looking for where the prefix run begins. _map itself is a plain Dictionary
    // (O(1) lookup/insert/remove) precisely because it no longer needs to maintain order.
    private readonly SortedSet<string> _sortedKeys = new(StringComparer.OrdinalIgnoreCase);

    // Read/write lock instead of a mutual-exclusion lock: lookups and directory enumerations
    // (the read-heavy majority) can proceed concurrently, and a full-scan enumeration no longer
    // blocks unrelated metadata lookups. Structural mutations still take the exclusive write lock.
    private readonly ReaderWriterLockSlim _syncRoot = new(LockRecursionPolicy.NoRecursion);

    private ulong _totalAllocated;

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
                _totalAllocated -= existing.FileInfo.AllocationSize;
            }
            else
            {
                _sortedKeys.Add(filePath);
            }

            node.FilePath = filePath;
            node.LeafName = ComputeLeafName(filePath);
            _map[filePath] = node;
            _totalAllocated += node.FileInfo.AllocationSize;
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
            _totalAllocated = 0;

            if (hasRoot)
            {
                _map["\\"] = root!;
                _sortedKeys.Add("\\");
                _totalAllocated = root!.FileInfo.AllocationSize;
            }
        }
        finally
        {
            _syncRoot.ExitWriteLock();
        }
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
    public IEnumerable<KeyValuePair<string, FileNode>> GetChildren(string dirPath, string? marker)
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

                // Only immediate children: no additional backslash after the prefix
                var childName = path[prefix.Length..];
                if (childName.Contains('\\'))
                {
                    continue;
                }

                if (marker != null &&
                    string.Compare(childName, marker, StringComparison.OrdinalIgnoreCase) <= 0)
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
    /// Returns the total number of bytes currently allocated across all nodes in the map.
    /// </summary>
    /// <returns>
    /// The sum of <see cref="Fsp.Interop.FileInfo.AllocationSize"/> for every stored node.
    /// </returns>
    public ulong GetTotalAllocated()
    {
        _syncRoot.EnterReadLock();
        try
        {
            return _totalAllocated;
        }
        finally
        {
            _syncRoot.ExitReadLock();
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
            if (_map.Remove(filePath, out var removed))
            {
                _sortedKeys.Remove(filePath);
                _totalAllocated -= removed.FileInfo.AllocationSize;
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
                _map[newKey] = descendant;
                _sortedKeys.Add(newKey);
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
    /// <param name="node">The node whose allocation size is changing.</param>
    /// <param name="newAllocationSize">The new allocation size, in bytes.</param>
    public void UpdateAllocationSize(FileNode node, ulong newAllocationSize)
    {
        _syncRoot.EnterWriteLock();
        try
        {
            _totalAllocated -= node.FileInfo.AllocationSize;
            node.FileInfo.AllocationSize = newAllocationSize;
            _totalAllocated += newAllocationSize;
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

    private static string ComputeLeafName(string filePath)
    {
        var lastSeparator = filePath.LastIndexOf('\\');
        return lastSeparator < 0 ? filePath : filePath[(lastSeparator + 1)..];
    }
}