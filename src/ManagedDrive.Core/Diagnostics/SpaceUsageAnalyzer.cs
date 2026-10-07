namespace ManagedDrive.Core.Diagnostics;

/// <summary>
/// A file or directory in a top list of <see cref="SpaceUsageReport"/>.
/// </summary>
/// <param name="Path">Full path on the disk.</param>
/// <param name="Allocated">Memory occupied, in bytes (streams included).</param>
/// <param name="Logical">Logical size, in bytes.</param>
/// <param name="FileCount">Number of files (1 for a file).</param>
public sealed record SpaceUsageEntry(string Path, ulong Allocated, ulong Logical, int FileCount);

/// <summary>
/// Space taken by the files of one extension.
/// </summary>
/// <param name="Extension">Lower-case extension with the dot, or <c>(none)</c>.</param>
/// <param name="FileCount">Number of files.</param>
/// <param name="Allocated">Memory occupied, in bytes.</param>
/// <param name="Logical">Logical size, in bytes.</param>
public sealed record SpaceUsageExtension(string Extension, int FileCount, ulong Allocated, ulong Logical);

/// <summary>
/// What is using the memory of a disk.
/// </summary>
/// <param name="TotalAllocated">Memory occupied by the whole disk; equals the disk's used bytes.</param>
/// <param name="TotalLogical">Sum of the logical file sizes.</param>
/// <param name="FileCount">Number of regular files.</param>
/// <param name="DirectoryCount">Number of directories, the root not counted.</param>
/// <param name="LinkCount">Number of symbolic links and junctions.</param>
/// <param name="StreamCount">Number of alternate data streams.</param>
/// <param name="TopDirectories">The directories using the most, largest first, each counting everything below it.</param>
/// <param name="TopFiles">The files using the most, largest first.</param>
/// <param name="TopExtensions">The extensions using the most, largest first.</param>
public sealed record SpaceUsageReport(
    ulong TotalAllocated,
    ulong TotalLogical,
    int FileCount,
    int DirectoryCount,
    int LinkCount,
    int StreamCount,
    IReadOnlyList<SpaceUsageEntry> TopDirectories,
    IReadOnlyList<SpaceUsageEntry> TopFiles,
    IReadOnlyList<SpaceUsageExtension> TopExtensions);

/// <summary>
/// Works out where a disk's memory goes from a snapshot of its nodes. Sizes are allocation sizes,
/// so the total equals the disk's used bytes; alternate data streams count towards their file.
/// </summary>
public static class SpaceUsageAnalyzer
{
    /// <summary>
    /// The extension reported for files without one.
    /// </summary>
    public const string NoExtension = "(none)";

    /// <summary>
    /// Most entries a top list may ask for.
    /// </summary>
    public const int MaxTop = 1000;

    /// <summary>
    /// Builds the directory tree with every node's sizes filled in and every child list sorted.
    /// </summary>
    /// <param name="nodes">The disk's nodes keyed by path, e.g. from <c>RamDisk.GetAllNodes()</c>.</param>
    /// <returns>The root node (<c>\</c>), which exists even if the input is empty.</returns>
    public static SpaceNode BuildTree(IReadOnlyList<KeyValuePair<string, FileNode>> nodes) => Build(nodes).Root;

    /// <summary>
    /// Summarises the disk: totals, the biggest directories and files, and space per extension.
    /// </summary>
    /// <param name="nodes">The disk's nodes keyed by path, e.g. from <c>RamDisk.GetAllNodes()</c>.</param>
    /// <param name="top">How many entries each top list holds, 1 to <see cref="MaxTop"/>.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="top"/> is out of range.</exception>
    public static SpaceUsageReport Analyze(IReadOnlyList<KeyValuePair<string, FileNode>> nodes, int top) =>
        Analyze(nodes, top, out _);

    /// <summary>
    /// Like <see cref="Analyze(IReadOnlyList{KeyValuePair{string, FileNode}}, int)"/>, and also hands
    /// back the tree the report was made from, so a caller needing both builds it only once.
    /// </summary>
    /// <param name="nodes">The disk's nodes keyed by path, e.g. from <c>RamDisk.GetAllNodes()</c>.</param>
    /// <param name="top">How many entries each top list holds, 1 to <see cref="MaxTop"/>.</param>
    /// <param name="root">Receives the root of the tree (see <see cref="BuildTree"/>).</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="top"/> is out of range.</exception>
    public static SpaceUsageReport Analyze(IReadOnlyList<KeyValuePair<string, FileNode>> nodes, int top, out SpaceNode root)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(top, MaxTop);

        var tree = Build(nodes);
        root = tree.Root;
        var files = new TopList(top);
        var directories = new TopList(top);
        var extensions = new Dictionary<string, (int Files, ulong Allocated, ulong Logical)>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in tree.All)
        {
            if (node.IsLink)
            {
                continue;
            }

            if (node.IsDirectory)
            {
                if (node != tree.Root)
                {
                    directories.Offer(node);
                }

                continue;
            }

            files.Offer(node);
            var extension = System.IO.Path.GetExtension(node.Name).ToLowerInvariant();
            if (extension.Length == 0)
            {
                extension = NoExtension;
            }

            var (count, allocated, logical) = extensions.GetValueOrDefault(extension);
            extensions[extension] = (count + 1, allocated + node.Allocated, logical + node.Logical);
        }

        var topExtensions = extensions
            .Select(pair => new SpaceUsageExtension(pair.Key, pair.Value.Files, pair.Value.Allocated, pair.Value.Logical))
            .OrderByDescending(entry => entry.Allocated)
            .ThenBy(entry => entry.Extension, StringComparer.Ordinal)
            .Take(top)
            .ToList();

        return new(
            tree.Root.Allocated,
            tree.Root.Logical,
            tree.Root.FileCount,
            tree.Directories,
            tree.Links,
            tree.Streams,
            directories.ToEntries(),
            files.ToEntries(),
            topExtensions);
    }

    /// <summary>
    /// The number of path separators in <paramref name="path"/>, i.e. how deep it sits.
    /// </summary>
    /// <param name="path">A disk path.</param>
    /// <returns>The depth; the root and its direct children are 0 and 1.</returns>
    private static int DepthOf(string path) => path.AsSpan().Count('\\');

    /// <summary>
    /// The directory part of <paramref name="path"/>.
    /// </summary>
    /// <param name="path">A disk path other than the root.</param>
    /// <returns>The parent path; <c>\</c> for a top-level entry.</returns>
    private static string ParentOf(string path)
    {
        var index = path.LastIndexOf('\\');
        return index <= 0 ? "\\" : path[..index];
    }

    /// <summary>
    /// Builds the tree and counts what it holds.
    /// </summary>
    /// <param name="nodes">The disk's nodes keyed by path.</param>
    /// <returns>The tree, its nodes in input order, and the counts.</returns>
    private static BuiltTree Build(IReadOnlyList<KeyValuePair<string, FileNode>> nodes)
    {
        var root = new SpaceNode { Name = "\\", Path = "\\", IsDirectory = true };
        var byPath = new Dictionary<string, SpaceNode>(StringComparer.OrdinalIgnoreCase) { ["\\"] = root };
        var all = new List<SpaceNode>(nodes.Count);
        var streams = new List<(string Owner, ulong Allocated, ulong Logical)>();
        var directories = 0;
        var links = 0;

        foreach (var (path, node) in nodes)
        {
            // FileInfo is a live struct; read it once so a concurrent write cannot give two answers.
            var info = node.FileInfo;

            if (path == "\\")
            {
                root.Allocated = info.AllocationSize;
                continue;
            }

            if (AlternateStreamName.IsStreamKey(path))
            {
                streams.Add((AlternateStreamName.OwnerOf(path), info.AllocationSize, info.FileSize));
                continue;
            }

            var isLink = node.ReparseData is not null;
            var isDirectory = node.IsDirectory && !isLink;
            var created = new SpaceNode
            {
                Name = path[(path.LastIndexOf('\\') + 1)..],
                Path = path,
                IsDirectory = isDirectory,
                IsLink = isLink,
                Allocated = info.AllocationSize,
                Logical = isDirectory || isLink ? 0 : info.FileSize,
                FileCount = isDirectory || isLink ? 0 : 1,
            };

            byPath[path] = created;
            all.Add(created);
            if (isLink)
            {
                links++;
            }
            else if (isDirectory)
            {
                directories++;
            }
        }

        // Streams belong to their file; one without a file still occupies memory, so it is charged
        // to the root and the total keeps matching the disk's used bytes.
        var streamCount = 0;
        foreach (var (owner, allocated, logical) in streams)
        {
            streamCount++;
            var target = byPath.GetValueOrDefault(owner) ?? root;
            target.Allocated += allocated;
            if (target != root && !target.IsDirectory)
            {
                target.Logical += logical;
            }
        }

        // Deepest first, so every node is complete before it is added to its parent.
        var byDepth = all.OrderByDescending(node => DepthOf(node.Path)).ToList();
        foreach (var node in byDepth)
        {
            var parent = byPath.GetValueOrDefault(ParentOf(node.Path)) is { IsDirectory: true } found ? found : root;
            node.Parent = parent;
            parent.ChildList.Add(node);
            parent.Allocated += node.Allocated;
            parent.Logical += node.Logical;
            parent.FileCount += node.FileCount;
        }

        foreach (var node in all.Append(root))
        {
            node.ChildList.Sort(CompareBySize);
        }

        return new(root, all, directories, links, streamCount);
    }

    /// <summary>
    /// Orders nodes largest first, ties by name.
    /// </summary>
    /// <param name="left">The first node.</param>
    /// <param name="right">The second node.</param>
    /// <returns>A negative number when <paramref name="left"/> comes first.</returns>
    private static int CompareBySize(SpaceNode left, SpaceNode right)
    {
        var bySize = right.Allocated.CompareTo(left.Allocated);
        return bySize != 0 ? bySize : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tree plus what <see cref="Analyze"/> needs besides it.
    /// </summary>
    /// <param name="Root">The root directory.</param>
    /// <param name="All">Every node except the root, in input order.</param>
    /// <param name="Directories">Number of directories without the root.</param>
    /// <param name="Links">Number of links.</param>
    /// <param name="Streams">Number of streams.</param>
    private sealed record BuiltTree(SpaceNode Root, List<SpaceNode> All, int Directories, int Links, int Streams);

    /// <summary>
    /// Keeps the <c>capacity</c> largest nodes offered to it, preferring earlier ones on ties.
    /// </summary>
    /// <param name="capacity">How many nodes to keep.</param>
    private sealed class TopList(int capacity)
    {
        /// <summary>
        /// A min-heap on size, so the smallest kept node is the one to replace. The sequence number
        /// makes a later node of equal size rank lower.
        /// </summary>
        private readonly PriorityQueue<SpaceNode, (ulong Size, long Rank)> _heap = new();

        /// <summary>
        /// How many nodes were offered so far.
        /// </summary>
        private long _offered;

        /// <summary>
        /// Considers a node for the list.
        /// </summary>
        /// <param name="node">The node.</param>
        public void Offer(SpaceNode node)
        {
            if (node.Allocated == 0)
            {
                return;
            }

            var priority = (node.Allocated, -_offered++);
            if (_heap.Count < capacity)
            {
                _heap.Enqueue(node, priority);
            }
            else if (_heap.TryPeek(out _, out var smallest) && priority.CompareTo(smallest) > 0)
            {
                _heap.DequeueEnqueue(node, priority);
            }
        }

        /// <summary>
        /// Returns what was kept, largest first (ties by path).
        /// </summary>
        /// <returns>The entries.</returns>
        public List<SpaceUsageEntry> ToEntries() => _heap.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(node => node.Allocated)
            .ThenBy(node => node.Path, StringComparer.OrdinalIgnoreCase)
            .Select(node => new SpaceUsageEntry(node.Path, node.Allocated, node.Logical, node.FileCount))
            .ToList();
    }
}
