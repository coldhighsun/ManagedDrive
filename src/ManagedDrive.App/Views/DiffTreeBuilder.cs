using System.ComponentModel;

namespace ManagedDrive.App.Views;

/// <summary>
/// One folder or file in the tree <see cref="SnapshotDiffDialog"/> shows. A node whose
/// <see cref="Kind"/> is <see langword="null"/> is only a container: a parent folder of changed
/// paths that is not changed itself.
/// </summary>
public sealed class DiffTreeNode : INotifyPropertyChanged
{
    /// <summary>
    /// Backing field of <see cref="IsExpanded"/>.
    /// </summary>
    private bool _isExpanded;

    /// <summary>
    /// Backing field of <see cref="CanExpand"/>.
    /// </summary>
    private bool _canExpand;

    /// <summary>
    /// Backing field of <see cref="ChangeCount"/>.
    /// </summary>
    private int _changeCount;

    /// <summary>
    /// Initializes a node.
    /// </summary>
    /// <param name="name">The path segment.</param>
    /// <param name="fullPath">The absolute path, e.g. <c>\Sub\a.txt</c>.</param>
    /// <param name="depth">The nesting level; top-level nodes are 0.</param>
    public DiffTreeNode(string name, string fullPath, int depth)
    {
        Name = name;
        FullPath = fullPath;
        Depth = depth;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the last path segment.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the absolute path of the node.
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    /// Gets the nesting level; top-level nodes are 0.
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// Gets or sets whether the node is a folder. Parents of other nodes are always folders.
    /// </summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// Gets or sets what restoring does to this path, or <see langword="null"/> for a container
    /// folder that is not changed itself.
    /// </summary>
    public DiffKind? Kind { get; set; }

    /// <summary>
    /// Gets the child nodes, sorted folders first and then by name.
    /// </summary>
    public List<DiffTreeNode> Children { get; } = [];

    /// <summary>
    /// Gets or sets whether the children are shown. Folders start collapsed.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
        }
    }

    /// <summary>
    /// Gets whether the node has children that pass the current filter, so the expander shows.
    /// </summary>
    public bool CanExpand => _canExpand;

    /// <summary>
    /// Gets how many changed files lie below this node under the current filter.
    /// </summary>
    public int ChangeCount => _changeCount;

    /// <summary>
    /// Gets the count text shown after a folder's name, or an empty string when there is none.
    /// </summary>
    public string CountText => IsDirectory && _changeCount > 0 ? $"({_changeCount})" : string.Empty;

    /// <summary>
    /// Gets the stroke geometry of the badge sign, or <see langword="null"/> for a container.
    /// </summary>
    public string? BadgeData => Kind switch
    {
        DiffKind.Restored => "M6,10 L14,10 M10,6 L10,14",
        DiffKind.Removed => "M6,10 L14,10",
        DiffKind.Reverted => "M5.5,11 C7.5,7.5 9,7.5 10,10 S12.5,12.5 14.5,9",
        _ => null
    };

    /// <summary>
    /// Gets the Segoe Fluent Icons glyph for a folder or a file.
    /// </summary>
    public string IconGlyph => IsDirectory ? "" : "";

    /// <summary>
    /// Stores what the last flatten computed for this node and raises the change notifications.
    /// </summary>
    /// <param name="canExpand">Whether any child is visible.</param>
    /// <param name="changeCount">The visible changed files below the node.</param>
    internal void UpdateVisibility(bool canExpand, int changeCount)
    {
        if (_canExpand != canExpand)
        {
            _canExpand = canExpand;
            PropertyChanged?.Invoke(this, new(nameof(CanExpand)));
        }

        if (_changeCount != changeCount)
        {
            _changeCount = changeCount;
            PropertyChanged?.Invoke(this, new(nameof(ChangeCount)));
            PropertyChanged?.Invoke(this, new(nameof(CountText)));
        }
    }
}

/// <summary>
/// Builds the folder tree of <see cref="SnapshotDiffDialog"/> from the changed paths and turns it
/// into the flat, indented list the dialog's <see cref="System.Windows.Controls.ListBox"/> shows.
/// </summary>
public static class DiffTreeBuilder
{
    /// <summary>
    /// Builds the tree: one node per path segment, with the unchanged parent folders of the
    /// changed paths as container nodes. All nodes start collapsed.
    /// </summary>
    /// <param name="rows">The changed paths.</param>
    /// <returns>The top-level nodes, sorted folders first and then by name.</returns>
    public static IReadOnlyList<DiffTreeNode> Build(IEnumerable<DiffRow> rows)
    {
        var roots = new List<DiffTreeNode>();
        var byPath = new Dictionary<string, DiffTreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var segments = row.FullPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            DiffTreeNode? parent = null;
            var path = string.Empty;

            for (var i = 0; i < segments.Length; i++)
            {
                path = $"{path}\\{segments[i]}";
                if (!byPath.TryGetValue(path, out var node))
                {
                    node = new(segments[i], path, i) { IsDirectory = true };
                    byPath[path] = node;
                    (parent?.Children ?? roots).Add(node);
                }

                parent = node;
            }

            if (parent is not null)
            {
                parent.Kind = row.Kind;
                parent.IsDirectory = row.IsDirectory;
            }
        }

        Sort(roots);
        return roots;
    }

    /// <summary>
    /// Flattens the tree into display order under <paramref name="filter"/>: a node shows when it
    /// has that kind itself or something below it does, and its children follow it only while it
    /// is expanded. Also refreshes each shown node's <see cref="DiffTreeNode.CanExpand"/> and
    /// <see cref="DiffTreeNode.ChangeCount"/>.
    /// </summary>
    /// <param name="roots">The top-level nodes from <see cref="Build"/>.</param>
    /// <param name="filter">The kind to show, or <see langword="null"/> for all.</param>
    /// <returns>The visible nodes in display order.</returns>
    public static List<DiffTreeNode> Flatten(IReadOnlyList<DiffTreeNode> roots, DiffKind? filter)
    {
        var output = new List<DiffTreeNode>();
        foreach (var root in roots)
        {
            Collect(root, filter, output, out _);
        }

        return output;
    }

    /// <summary>
    /// Sorts nodes folders first and then by name, recursively.
    /// </summary>
    /// <param name="nodes">The siblings to sort.</param>
    private static void Sort(List<DiffTreeNode> nodes)
    {
        nodes.Sort(static (a, b) =>
        {
            var byKind = b.IsDirectory.CompareTo(a.IsDirectory);
            return byKind != 0 ? byKind : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });

        foreach (var node in nodes)
        {
            Sort(node.Children);
        }
    }

    /// <summary>
    /// Appends <paramref name="node"/> and its expanded descendants to <paramref name="output"/>
    /// when the node passes the filter.
    /// </summary>
    /// <param name="node">The node to add.</param>
    /// <param name="filter">The kind to show, or <see langword="null"/> for all.</param>
    /// <param name="output">The list in display order.</param>
    /// <param name="changeCount">The visible changed files in the node's subtree, itself included.</param>
    /// <returns>Whether the node is visible.</returns>
    private static bool Collect(DiffTreeNode node, DiffKind? filter, List<DiffTreeNode> output, out int changeCount)
    {
        var childRows = new List<DiffTreeNode>();
        var childCount = 0;
        var hasVisibleChild = false;

        foreach (var child in node.Children)
        {
            if (Collect(child, filter, childRows, out var count))
            {
                hasVisibleChild = true;
                childCount += count;
            }
        }

        var matches = filter is null || node.Kind == filter;
        changeCount = childCount + (matches && !node.IsDirectory && node.Kind is not null ? 1 : 0);
        if (!matches && !hasVisibleChild)
        {
            return false;
        }

        node.UpdateVisibility(hasVisibleChild, changeCount);
        output.Add(node);
        if (node.IsExpanded)
        {
            output.AddRange(childRows);
        }

        return true;
    }
}
