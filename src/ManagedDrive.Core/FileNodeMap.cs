namespace ManagedDrive.Core;

/// <summary>
/// Thread-safe, case-insensitive path-to-<see cref="FileNode"/> map that models the directory
/// tree of the in-memory file system. Keys are absolute paths using <c>\</c> as the separator
/// (e.g., <c>\Folder\File.txt</c>). The root directory is stored under the key <c>\</c>.
/// </summary>
public sealed class FileNodeMap
{
    private readonly SortedDictionary<string, FileNode> _map = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _syncRoot = new();

    /// <summary>
    /// Gets the number of nodes currently stored in the map.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_syncRoot)
            {
                return _map.Count;
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
        lock (_syncRoot)
        {
            node.FilePath = filePath;
            _map[filePath] = node;
        }
    }

    /// <summary>
    /// Returns a snapshot of all nodes in the map, in sorted path order.
    /// </summary>
    /// <returns>
    /// A sequence of all (path, node) pairs currently stored in the map.
    /// </returns>
    public IEnumerable<KeyValuePair<string, FileNode>> GetAllNodes()
    {
        lock (_syncRoot)
        {
            return new List<KeyValuePair<string, FileNode>>(_map);
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
        List<KeyValuePair<string, FileNode>> snapshot;
        lock (_syncRoot)
        {
            snapshot = new List<KeyValuePair<string, FileNode>>(_map);
        }

        // For root "\" (length 1) the prefix equals dirPath itself; for others append "\"
        var prefix = dirPath.Length == 1 ? dirPath : (dirPath + "\\");

        foreach (var kvp in snapshot)
        {
            var path = kvp.Key;

            if (string.Equals(path, dirPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
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

            yield return kvp;
        }
    }

    /// <summary>
    /// Returns the total number of bytes currently allocated across all nodes in the map.
    /// </summary>
    /// <returns>
    /// The sum of <see cref="Fsp.Interop.FileInfo.AllocationSize"/> for every stored node.
    /// </returns>
    public ulong GetTotalAllocated()
    {
        lock (_syncRoot)
        {
            ulong total = 0;
            foreach (var node in _map.Values)
            {
                total += node.FileInfo.AllocationSize;
            }
            return total;
        }
    }

    /// <summary>
    /// Removes all nodes except the root directory entry (<c>\</c>).
    /// </summary>
    public void ClearAll()
    {
        lock (_syncRoot)
        {
            var toRemove = new List<string>();
            foreach (var key in _map.Keys)
            {
                if (key != "\\")
                {
                    toRemove.Add(key);
                }
            }

            foreach (var key in toRemove)
            {
                _map.Remove(key);
            }
        }
    }

    /// <summary>
    /// Removes the node at <paramref name="filePath"/>, if present.
    /// </summary>
    /// <param name="filePath">Absolute file-system path.</param>
    public void Remove(string filePath)
    {
        lock (_syncRoot)
        {
            _map.Remove(filePath);
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
        lock (_syncRoot)
        {
            var prefix = oldPath + "\\";
            var keys = new List<string>();
            foreach (var key in _map.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(key);
                }
            }

            foreach (var key in keys)
            {
                var descendant = _map[key];
                _map.Remove(key);
                var newKey = newPath + key.Substring(oldPath.Length);
                descendant.FilePath = newKey;
                _map[newKey] = descendant;
            }
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
        lock (_syncRoot)
        {
            return _map.TryGetValue(filePath, out node);
        }
    }
}