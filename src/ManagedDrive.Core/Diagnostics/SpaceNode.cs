namespace ManagedDrive.Core.Diagnostics;

/// <summary>
/// One file or directory in the tree built by <see cref="SpaceUsageAnalyzer.BuildTree"/>. A
/// directory's sizes cover everything below it; a file's sizes include its alternate data streams.
/// </summary>
public sealed class SpaceNode
{
    /// <summary>
    /// The children, sorted largest first by the analyzer once the tree is complete.
    /// </summary>
    internal List<SpaceNode> ChildList { get; } = [];

    /// <summary>
    /// The name without any directory part; the root's name is <c>\</c>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The full path on the disk, e.g. <c>\Folder\a.txt</c>; <c>\</c> for the root.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Whether this is a directory (the root included).
    /// </summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// Whether this is a symbolic link or junction. Links occupy no space of their own.
    /// </summary>
    public bool IsLink { get; init; }

    /// <summary>
    /// The enclosing directory, or <c>null</c> for the root.
    /// </summary>
    public SpaceNode? Parent { get; internal set; }

    /// <summary>
    /// Space the node and everything below it occupies in memory, in bytes (the same measure as
    /// the disk's used bytes: allocation size, streams included).
    /// </summary>
    public ulong Allocated { get; internal set; }

    /// <summary>
    /// The logical size of the node and everything below it, in bytes (what Explorer shows).
    /// </summary>
    public ulong Logical { get; internal set; }

    /// <summary>
    /// Number of regular files at or below this node (a file counts itself).
    /// </summary>
    public int FileCount { get; internal set; }

    /// <summary>
    /// The children, largest first (ties by name); empty for a file.
    /// </summary>
    public IReadOnlyList<SpaceNode> Children => ChildList;
}
