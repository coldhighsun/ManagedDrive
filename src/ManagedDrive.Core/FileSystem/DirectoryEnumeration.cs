using FileInfo = Fsp.Interop.FileInfo;

namespace ManagedDrive.Core.FileSystem;

/// <summary>
/// Builds and iterates the directory-entry list for a WinFsp <c>ReadDirectoryEntry</c> sequence.
/// Extracted from <see cref="MemoryFileSystem"/> so the enumeration logic lives in one place.
/// The returned <see cref="DirContext"/> is a stateless snapshot iterated across successive
/// <c>ReadDirectoryEntry</c> calls.
/// </summary>
internal static class DirectoryEnumeration
{
    /// <summary>
    /// Builds the full directory-entry list for <paramref name="dir"/> at the start of a
    /// <c>ReadDirectoryEntry</c> sequence, including <c>.</c> and <c>..</c>.
    /// </summary>
    /// <param name="nodeMap">The map to resolve children and the parent directory from.</param>
    /// <param name="dir">The directory node being enumerated.</param>
    /// <param name="pattern">Optional glob filter applied to child names.</param>
    /// <param name="marker">Optional resume marker naming the last entry already returned.</param>
    /// <returns>
    /// A <see cref="DirContext"/> snapshot positioned before the first entry.
    /// </returns>
    internal static DirContext Build(FileNodeMap nodeMap, FileNode dir, string? pattern, string? marker)
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
                if (!nodeMap.TryGet(parentPath, out var p) || p == null)
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

        foreach (var kvp in nodeMap.GetChildren(dir.FilePath, childMarker))
        {
            var childName = kvp.Value.LeafName;
            if (WildcardMatcher.Matches(pattern, childName))
            {
                entries.Add((childName, kvp.Value.FileInfo));
            }
        }

        return new(entries);
    }
}

/// <summary>
/// Stateless snapshot of directory entries, iterated across successive
/// <c>ReadDirectoryEntry</c> calls via <see cref="TryNext"/>.
/// </summary>
internal sealed class DirContext
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
