using ManagedDrive.Cli.Core;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="DiskContentDialog"/>. Read-only view of a mounted disk's
/// files and directories, laid out as a flattened, expandable list with aligned Name/Size/Type
/// columns (a "poor man's TreeListView": a real <see cref="System.Windows.Controls.TreeView"/>
/// indents its entire row per nesting depth, which misaligns the Size/Type columns for anything
/// below the top level; a flat <see cref="System.Windows.Controls.ListView"/>/<see cref="System.Windows.Controls.GridView"/>
/// keeps those columns aligned and only indents the Name cell's content).
/// </summary>
public partial class DiskContentDialog
{
    private readonly HashSet<DiskContentNode> _expandedNodes = [];
    private readonly ObservableCollection<DiskContentRow> _rows = [];
    private List<DiskContentNode> _rootNodes = [];
    private bool _sortAscending = true;
    private SortKey _sortKey = SortKey.Name;

    /// <summary>
    /// Initializes the dialog with a snapshot of <paramref name="target"/>'s current contents.
    /// </summary>
    /// <param name="target">The disk whose contents to display.</param>
    public DiskContentDialog(DiskViewModel target)
    {
        InitializeComponent();

        // Override the zero resize border DialogWindowBase's constructor set (most dialogs are
        // fixed-size), so this window alone can be resized by dragging its edges.
        WindowChrome.SetWindowChrome(this, new()
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new(6),
            GlassFrameThickness = new(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
        });

        var nodes = target.Disk.GetAllNodes();
        var root = BuildTree(nodes);
        _rootNodes = root.Children.Values.Select(ToNode).ToList();

        SummaryText.Text = Loc.Format(
            "DiskContent.TotalUsage",
            ByteFormatter.Format(target.Disk.UsedBytes),
            ByteFormatter.Format(target.Disk.TotalBytes));

        if (_rootNodes.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            ContentList.Visibility = Visibility.Collapsed;
        }
        else
        {
            SortRecursively(_rootNodes, BuildComparer(_sortKey, _sortAscending));
            RebuildRows();
            UpdateSortArrows();
            ContentList.ItemsSource = _rows;
        }
    }

    private enum SortKey
    {
        Name,
        Size,
        Type,
    }

    /// <summary>
    /// Builds a comparer for the given sort key/direction, used to sort a node's children
    /// (recursively, level by level — see <see cref="SortRecursively"/>) rather than the
    /// flattened row list, so parent/child grouping is preserved.
    /// </summary>
    private static IComparer<DiskContentNode> BuildComparer(SortKey key, bool ascending)
    {
        Comparison<DiskContentNode> compare = key switch
        {
            SortKey.Size => (a, b) => a.SizeBytes.CompareTo(b.SizeBytes),
            SortKey.Type => (a, b) => string.Compare(a.TypeDisplay, b.TypeDisplay, StringComparison.CurrentCultureIgnoreCase),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
        };

        return Comparer<DiskContentNode>.Create(ascending ? compare : (a, b) => compare(b, a));
    }

    /// <summary>
    /// Nests every node's full path into a directory tree rooted at <c>\</c>, computing each
    /// directory's size as the sum of its descendant files' <see cref="Fsp.Interop.FileInfo.FileSize"/>.
    /// </summary>
    private static TreeBuilder BuildTree(IReadOnlyList<KeyValuePair<string, FileNode>> nodes)
    {
        var root = new TreeBuilder("\\", isDirectory: true);

        foreach (var (path, node) in nodes)
        {
            if (path == "\\")
            {
                continue;
            }

            var current = root;
            var segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (!current.Children.TryGetValue(segment, out var child))
                {
                    child = new TreeBuilder(segment, isDirectory: true);
                    current.Children[segment] = child;
                }

                current = child;
            }

            current.IsDirectory = node.IsDirectory;
            if (!node.IsDirectory)
            {
                current.SizeBytes = node.FileInfo.FileSize;
            }
        }

        PropagateSizes(root);
        return root;
    }

    /// <summary>
    /// Walks up the visual tree from <paramref name="source"/> to find the nearest ancestor of
    /// type <typeparamref name="T"/> — e.g. the <see cref="GridViewColumnHeader"/> that raised a
    /// bubbled <c>Click</c> event, since the original source is usually a child element like its
    /// auto-generated <c>TextBlock</c>, not the header itself.
    /// </summary>
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        return source as T;
    }

    /// <summary>
    /// Recursively sums each directory's own <see cref="TreeBuilder.SizeBytes"/> from its
    /// children's sizes (files contribute their own size; already-set for leaves).
    /// </summary>
    private static ulong PropagateSizes(TreeBuilder node)
    {
        if (!node.IsDirectory)
        {
            return node.SizeBytes;
        }

        ulong total = 0;
        foreach (var child in node.Children.Values)
        {
            total += PropagateSizes(child);
        }

        node.SizeBytes = total;
        return total;
    }

    /// <summary>
    /// Sorts <paramref name="nodes"/> in place using <paramref name="comparer"/>, then recursively
    /// sorts each node's own children the same way — a per-level sort (like Explorer's column
    /// sorting) rather than a sort of the flattened row list, so parent/child grouping survives.
    /// </summary>
    private static void SortRecursively(List<DiskContentNode> nodes, IComparer<DiskContentNode> comparer)
    {
        nodes.Sort(comparer);
        foreach (var node in nodes)
        {
            SortRecursively(node.Children, comparer);
        }
    }

    private static DiskContentNode ToNode(TreeBuilder builder) =>
        new(builder.Name, builder.IsDirectory, builder.SizeBytes, builder.Children.Values.Select(ToNode));

    private void AddRows(IEnumerable<DiskContentNode> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            var expanded = _expandedNodes.Contains(node);
            var row = new DiskContentRow(node, depth);
            if (expanded)
            {
                row.SetExpanded(true);
            }

            _rows.Add(row);

            if (expanded)
            {
                AddRows(node.Children, depth + 1);
            }
        }
    }

    /// <summary>
    /// Handles a click on any of the <c>ListView</c>'s <see cref="GridViewColumnHeader"/>s
    /// (attached via the <c>GridViewColumnHeader.Click</c> routed event on the <c>ListView</c>
    /// itself): sorts by the clicked column, toggling direction if it's already the active column.
    /// </summary>
    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) is not { Column: { } column } ||
            ResolveSortKey(column) is not { } key)
        {
            return;
        }

        _sortAscending = _sortKey == key ? !_sortAscending : true;
        _sortKey = key;

        SortRecursively(_rootNodes, BuildComparer(_sortKey, _sortAscending));
        RebuildRows();
        UpdateSortArrows();
    }

    private void ExpanderButton_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is DiskContentRow row)
        {
            ToggleExpanded(row);
        }
    }

    /// <summary>
    /// Rebuilds <see cref="_rows"/> from <see cref="_rootNodes"/> in their current sort order,
    /// descending into a node's children only while that node is present in
    /// <see cref="_expandedNodes"/>.
    /// </summary>
    private void RebuildRows()
    {
        _rows.Clear();
        AddRows(_rootNodes, depth: 0);
    }

    private SortKey? ResolveSortKey(object column) =>
            column == NameColumn ? SortKey.Name :
            column == SizeColumn ? SortKey.Size :
            column == TypeColumn ? SortKey.Type :
            null;

    /// <summary>
    /// Expands or collapses a directory row when it's double-clicked anywhere except the
    /// expander button itself (whose own <c>Click</c> handler already toggles it — handling it
    /// again here would just flip it straight back).
    /// </summary>
    private void Row_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (((ListViewItem)sender).Content is not DiskContentRow row || !row.HasChildren)
        {
            return;
        }

        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is { Name: "ExpanderButton" })
        {
            return;
        }

        ToggleExpanded(row);
    }

    /// <summary>
    /// Toggles the given row's node between expanded and collapsed and rebuilds the flattened
    /// row list to match.
    /// </summary>
    private void ToggleExpanded(DiskContentRow row)
    {
        if (!_expandedNodes.Remove(row.Node))
        {
            _expandedNodes.Add(row.Node);
        }

        RebuildRows();
    }

    /// <summary>
    /// Shows a chevron next to the active sort column's header text (pointing up for ascending,
    /// down for descending) and hides it on the other two columns.
    /// </summary>
    private void UpdateSortArrows()
    {
        var ascendingGlyph = ((char)0xE96D).ToString();
        var descendingGlyph = ((char)0xE96E).ToString();

        NameSortArrow.Visibility = Visibility.Collapsed;
        SizeSortArrow.Visibility = Visibility.Collapsed;
        TypeSortArrow.Visibility = Visibility.Collapsed;

        var arrow = _sortKey switch
        {
            SortKey.Size => SizeSortArrow,
            SortKey.Type => TypeSortArrow,
            _ => NameSortArrow,
        };

        arrow.Text = _sortAscending ? ascendingGlyph : descendingGlyph;
        arrow.Visibility = Visibility.Visible;
    }

    private sealed class TreeBuilder(string name, bool isDirectory)
    {
        public SortedDictionary<string, TreeBuilder> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsDirectory { get; set; } = isDirectory;
        public string Name { get; } = name;

        public ulong SizeBytes
        {
            get; set;
        }
    }
}

/// <summary>
/// One folder or file node in <see cref="DiskContentDialog"/>'s tree view, with its display name,
/// formatted size, and (for directories) its children. <see cref="Children"/>'s order is not
/// fixed at construction — <see cref="DiskContentDialog"/> sorts it (and every descendant level)
/// in place whenever the user changes the sort column.
/// </summary>
public sealed class DiskContentNode
{
    /// <summary>
    /// Initializes a node, deriving <see cref="TypeDisplay"/> from <paramref name="isDirectory"/>
    /// and the file extension in <paramref name="name"/>.
    /// </summary>
    public DiskContentNode(string name, bool isDirectory, ulong sizeBytes, IEnumerable<DiskContentNode> children)
    {
        Name = name;
        IsDirectory = isDirectory;
        SizeDisplay = ByteFormatter.Format(sizeBytes);
        TypeDisplay = BuildTypeDisplay(name, isDirectory);
        Children = [.. children];
        SizeBytes = sizeBytes;
    }

    /// <summary>
    /// Gets this node's children; empty for files. Re-sorted in place by
    /// <see cref="DiskContentDialog"/> when the user changes the active sort column.
    /// </summary>
    public List<DiskContentNode> Children
    {
        get;
    }

    /// <summary>
    /// Gets whether this node represents a directory, used to pick the row's icon.
    /// </summary>
    public bool IsDirectory
    {
        get;
    }

    /// <summary>
    /// Gets the node's display name (its path's last segment).
    /// </summary>
    public string Name
    {
        get;
    }

    /// <summary>
    /// Gets this node's size in bytes, used only for sorting siblings.
    /// </summary>
    public ulong SizeBytes
    {
        get;
    }

    /// <summary>
    /// Gets the human-readable formatted size shown next to <see cref="Name"/>.
    /// </summary>
    public string SizeDisplay
    {
        get;
    }

    /// <summary>
    /// Gets the Explorer-style type label shown in the "Type" column: "File folder" for
    /// directories, "{EXT} File" for files with an extension, or a generic "File" fallback.
    /// </summary>
    public string TypeDisplay
    {
        get;
    }

    private static string BuildTypeDisplay(string name, bool isDirectory)
    {
        if (isDirectory)
        {
            return Loc.Get("DiskContent.TypeFolder");
        }

        var ext = Path.GetExtension(name).TrimStart('.');
        return ext.Length > 0
            ? Loc.Format("DiskContent.TypeFile", ext.ToUpperInvariant())
            : Loc.Get("DiskContent.TypeFileGeneric");
    }
}

/// <summary>
/// A single visible row in <see cref="DiskContentDialog"/>'s flattened list: a
/// <see cref="DiskContentNode"/> plus its nesting depth (used only to indent the Name cell's
/// content, not the whole row) and current expand/collapse state.
/// </summary>
public sealed class DiskContentRow(DiskContentNode node, int depth) : INotifyPropertyChanged
{
    private bool _isExpanded;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets this row's nesting depth (0 for top-level nodes), used to indent the Name column.
    /// </summary>
    public int Depth { get; } = depth;

    /// <summary>
    /// Gets whether <see cref="Node"/> has any children, controlling whether the expander is shown.
    /// </summary>
    public bool HasChildren => Node.Children.Count > 0;

    /// <summary>
    /// Gets whether this row's children are currently inserted into the flattened list.
    /// </summary>
    public bool IsExpanded => _isExpanded;

    /// <summary>
    /// Gets the underlying node this row displays.
    /// </summary>
    public DiskContentNode Node { get; } = node;

    /// <summary>
    /// Updates <see cref="IsExpanded"/> and raises <see cref="PropertyChanged"/> so the
    /// expander glyph flips direction.
    /// </summary>
    public void SetExpanded(bool value)
    {
        _isExpanded = value;
        PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
    }
}

/// <summary>
/// Converts a <see cref="DiskContentRow.Depth"/> into a left <see cref="Thickness"/> so the
/// Name column's expander/icon/text indent by nesting level without affecting the Size/Type
/// columns, which stay aligned across all rows.
/// </summary>
public sealed class DepthToIndentConverter : IValueConverter
{
    private const double IndentPerLevel = 16;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness((value is int depth ? depth : 0) * IndentPerLevel, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}