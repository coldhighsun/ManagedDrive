using ManagedDrive.Cli.Core;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly bool _isReadOnly;
    private readonly string _mountPoint;
    private readonly List<DiskContentNode> _rootNodes = [];
    private readonly ObservableCollection<DiskContentRow> _rows = [];
    private readonly DiskViewModel _target;
    private CancellationTokenSource? _deleteCts;
    private bool _sortAscending = true;
    private SortKey _sortKey = SortKey.Name;

    /// <summary>
    /// Initializes the dialog with a snapshot of <paramref name="target"/>'s current contents.
    /// </summary>
    /// <param name="target">The disk whose contents to display.</param>
    public DiskContentDialog(DiskViewModel target)
    {
        InitializeComponent();

        _target = target;
        _isReadOnly = target.IsReadOnly;
        _mountPoint = target.Disk.MountPoint;

        // Override the zero resize border DialogWindowBase's constructor set (most dialogs are
        // fixed-size), so this window alone can be resized by dragging its edges.
        WindowChrome.SetWindowChrome(this, new()
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new(6),
            GlassFrameThickness = new(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
        });

        // Only resizable dialog in the app, so it's the only one that can be maximized — without
        // this, the borderless + transparent window ignores the taskbar's work area and covers it.
        WindowMaximizeHelper.HookMaximizeBehavior(this);

        // Cancel any in-flight delete instead of leaving it to keep deleting files after the
        // dialog (and its close-button/context-menu-driven cancellation surface) is gone — fires
        // for every close path (title bar X, bottom Close button, Esc), since Window.Closing is
        // the common point they all funnel through.
        Closing += (_, _) => _deleteCts?.Cancel();

        var nodes = target.Disk.GetAllNodes();
        var root = BuildTree(nodes);
        _rootNodes = root.Children.Values.Select(child => ToNode(child, "\\" + child.Name)).ToList();

        UpdateSummaryText();

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
    /// Counts the files under <paramref name="directoryFullPath"/> (recursively), used only to
    /// populate the delete overlay's "x / total" progress text. Best-effort: any enumeration
    /// failure (e.g. a file becoming inaccessible mid-scan) just falls back to an unknown total,
    /// since the real error handling happens during the actual delete pass.
    /// </summary>
    private static int CountFilesSafe(string directoryFullPath)
    {
        try
        {
            return Directory.EnumerateFiles(directoryFullPath, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Drops any node whose path falls under another selected directory node, so a selection
    /// containing both a folder and its own descendants doesn't attempt to delete the descendant
    /// a second time after the folder's recursive delete already removed it.
    /// </summary>
    private static List<DiskContentNode> ExcludeDescendantsOfSelectedDirectories(List<DiskContentNode> nodes)
    {
        var selectedDirectoryPaths = nodes.Where(n => n.IsDirectory).Select(n => n.FullPath).ToList();

        return nodes.Where(node => !selectedDirectoryPaths.Any(dirPath =>
            !string.Equals(dirPath, node.FullPath, StringComparison.OrdinalIgnoreCase) &&
            node.FullPath.StartsWith(dirPath + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
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
    /// Walks the visual tree from a context-menu <see cref="MenuItem"/> up to the
    /// <see cref="ContextMenu"/>'s <see cref="ContextMenu.PlacementTarget"/> (the
    /// <see cref="ListViewItem"/> that was right-clicked) and returns its bound row.
    /// </summary>
    private static DiskContentRow? GetRowFromMenuItem(object sender) =>
        ((MenuItem)sender).Parent is ContextMenu { PlacementTarget: ListViewItem { DataContext: DiskContentRow row } }
            ? row
            : null;

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

    private static bool RemoveNode(List<DiskContentNode> siblings, DiskContentNode node)
    {
        if (siblings.Remove(node))
        {
            return true;
        }

        foreach (var sibling in siblings)
        {
            if (RemoveNode(sibling.Children, node))
            {
                return true;
            }
        }

        return false;
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

    private static DiskContentNode ToNode(TreeBuilder builder, string path) =>
        new(builder.Name, builder.IsDirectory, builder.SizeBytes, path,
            builder.Children.Values.Select(child => ToNode(child, path + "\\" + child.Name)));

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

        _sortAscending = _sortKey != key || !_sortAscending;
        _sortKey = key;

        SortRecursively(_rootNodes, BuildComparer(_sortKey, _sortAscending));
        RebuildRows();
        UpdateSortArrows();
    }

    /// <summary>
    /// Deletes every selected row's node from the mounted disk (via its real filesystem path, so
    /// the WinFsp <c>CanDelete</c>/<c>Cleanup</c> callbacks handle dirty-tracking and capacity
    /// accounting exactly as they would for any other client), after a single confirmation
    /// prompt covering the whole selection. The actual delete I/O runs off the UI thread behind
    /// <see cref="DeleteOverlay"/>, since a recursive directory delete of many files can take a
    /// noticeable amount of time.
    /// </summary>
    private async void DeleteNode_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly)
        {
            return;
        }

        var selectedNodes = ContentList.SelectedItems.Cast<DiskContentRow>().Select(r => r.Node).ToList();
        if (selectedNodes.Count == 0)
        {
            if (GetRowFromMenuItem(sender) is not { } fallbackRow)
            {
                return;
            }

            selectedNodes = [fallbackRow.Node];
        }

        var nodesToDelete = ExcludeDescendantsOfSelectedDirectories(selectedNodes);
        if (nodesToDelete.Count == 0)
        {
            return;
        }

        var confirmBody = nodesToDelete.Count == 1
            ? (nodesToDelete[0].IsDirectory
                ? Loc.Format("Msg.DeleteNodeConfirmBodyFolder", nodesToDelete[0].Name)
                : Loc.Format("Msg.DeleteNodeConfirmBodyFile", nodesToDelete[0].Name))
            : Loc.Format("Msg.DeleteNodeConfirmBodyMultiple", nodesToDelete.Count);

        var confirm = new ConfirmDialog(Loc.Get("Msg.DeleteNodeConfirmTitle"), confirmBody)
        {
            Owner = this,
        };

        if (confirm.ShowDialog() != true)
        {
            return;
        }

        IProgress<(int Completed, int Total)> progress =
            new Progress<(int Completed, int Total)>(p => UpdateDeleteProgressText(p.Completed, p.Total));
        ShowDeleteOverlay();

        _deleteCts = new CancellationTokenSource();
        var token = _deleteCts.Token;

        var deletedNodes = new List<DiskContentNode>();
        (string Name, string Message)? failure = null;

        try
        {
            await Task.Run(() =>
            {
                // Counted (and later enumerated) live off the real filesystem rather than the
                // dialog's node-tree snapshot, so the total stays accurate even if the disk's
                // contents changed after the dialog was opened.
                var totalFiles = nodesToDelete.Sum(node => node.IsDirectory ? CountFilesSafe(ToRealPath(node.FullPath)) : 1);
                var completed = 0;
                progress.Report((completed, totalFiles));

                foreach (var node in nodesToDelete)
                {
                    token.ThrowIfCancellationRequested();

                    var fullPath = ToRealPath(node.FullPath);

                    try
                    {
                        if (node.IsDirectory)
                        {
                            foreach (var filePath in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                            {
                                token.ThrowIfCancellationRequested();
                                File.Delete(filePath);
                                completed++;
                                progress.Report((completed, totalFiles));
                            }

                            Directory.Delete(fullPath, recursive: true);
                        }
                        else
                        {
                            File.Delete(fullPath);
                            completed++;
                            progress.Report((completed, totalFiles));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failure = (node.Name, ex.Message);
                        break;
                    }

                    deletedNodes.Add(node);
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            // The dialog is closing (see the Closing handler in the constructor) — no point
            // updating the now-departing UI for whatever got deleted before cancellation.
            return;
        }
        finally
        {
            _deleteCts.Dispose();
            _deleteCts = null;
            HideDeleteOverlay();
        }

        foreach (var node in deletedNodes)
        {
            RemoveNode(node);
            _expandedNodes.Remove(node);
        }

        RebuildRows();
        UpdateSummaryText();

        if (failure is { } f)
        {
            new ConfirmDialog(Loc.Get("Msg.DeleteNodeConfirmTitle"), Loc.Format("Msg.DeleteNodeFailed", f.Name, f.Message))
            {
                Owner = this,
            }.ShowDialog();
        }
    }

    private void ExpanderButton_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is DiskContentRow row)
        {
            ToggleExpanded(row);
        }
    }

    /// <summary>
    /// Hides <see cref="DeleteOverlay"/> and stops its spinner animation.
    /// </summary>
    private void HideDeleteOverlay()
    {
        DeleteSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        DeleteOverlay.Visibility = Visibility.Collapsed;
        ContentList.IsEnabled = true;
    }

    /// <summary>
    /// Opens the row's node in Explorer: directories are opened directly, files are opened with
    /// <c>/select,</c> so Explorer highlights the file within its parent folder.
    /// </summary>
    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (GetRowFromMenuItem(sender) is not { } row)
        {
            return;
        }

        var fullPath = ToRealPath(row.Node.FullPath);

        Process.Start("explorer.exe", row.Node.IsDirectory ? fullPath : $"/select,\"{fullPath}\"");
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

    /// <summary>
    /// Removes <paramref name="node"/> from whichever list in <see cref="_rootNodes"/> (or a
    /// descendant's <see cref="DiskContentNode.Children"/>) currently holds it, by reference.
    /// </summary>
    private bool RemoveNode(DiskContentNode node) =>
        RemoveNode(_rootNodes, node);

    private SortKey? ResolveSortKey(object column) =>
                Equals(column, NameColumn) ? SortKey.Name :
                Equals(column, SizeColumn) ? SortKey.Size :
                Equals(column, TypeColumn) ? SortKey.Type :
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
    /// Mimics Explorer's right-click selection behavior: right-clicking a row that's already
    /// part of the current multi-selection leaves the selection untouched (so the context menu's
    /// "Delete" applies to the whole selection), while right-clicking outside it collapses the
    /// selection down to just that row.
    /// </summary>
    private void Row_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((ListViewItem)sender).Content is not DiskContentRow row)
        {
            return;
        }

        if (!ContentList.SelectedItems.Contains(row))
        {
            ContentList.SelectedItem = row;
        }
    }

    /// <summary>
    /// Disables the context menu's "Delete" entry when the disk is read-only, since attempting
    /// the delete would fail anyway (WinFsp returns <c>STATUS_MEDIA_WRITE_PROTECTED</c>) — this
    /// gives the user feedback up front instead of a failed-delete message box.
    /// </summary>
    private void RowContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (((ContextMenu)sender).Items.OfType<MenuItem>().LastOrDefault() is { } deleteItem)
        {
            deleteItem.IsEnabled = !_isReadOnly;
        }
    }

    /// <summary>
    /// Shows <see cref="DeleteOverlay"/> over the content list and starts its spinner spinning,
    /// also disabling the list so the selection can't change mid-delete.
    /// </summary>
    private void ShowDeleteOverlay()
    {
        ContentList.IsEnabled = false;
        DeleteProgressText.Text = Loc.Get("DiskContent.Deleting");
        DeleteOverlay.Visibility = Visibility.Visible;
        DeleteSpinnerRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
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
    /// Converts a disk-relative virtual path (e.g. <c>\Folder\File.txt</c>) into a real
    /// filesystem path under this disk's mount point.
    /// </summary>
    private string ToRealPath(string virtualPath) =>
        Path.Combine(_mountPoint, virtualPath.TrimStart('\\').Replace('\\', Path.DirectorySeparatorChar));

    /// <summary>
    /// Updates the delete overlay's status text with a "x / total" file count, or falls back to
    /// the plain "Deleting..." text when <paramref name="total"/> is unknown/zero (e.g. the
    /// selection is only empty directories, or the up-front file count failed).
    /// </summary>
    private void UpdateDeleteProgressText(int completed, int total) =>
        DeleteProgressText.Text = total > 0
            ? Loc.Format("DiskContent.DeletingProgress", completed, total)
            : Loc.Get("DiskContent.Deleting");

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

    private void UpdateSummaryText() =>
        SummaryText.Text = Loc.Format(
            "DiskContent.TotalUsage",
            ByteFormatter.Format(_target.Disk.UsedBytes),
            ByteFormatter.Format(_target.Disk.TotalBytes));

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
    public DiskContentNode(string name, bool isDirectory, ulong sizeBytes, string fullPath, IEnumerable<DiskContentNode> children)
    {
        Name = name;
        IsDirectory = isDirectory;
        SizeDisplay = ByteFormatter.Format(sizeBytes);
        TypeDisplay = BuildTypeDisplay(name, isDirectory);
        Children = [.. children];
        SizeBytes = sizeBytes;
        FullPath = fullPath;
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
    /// Gets this node's full virtual path on the disk (e.g. <c>\Folder\File.txt</c>), used to
    /// locate the corresponding real file under the disk's mount point.
    /// </summary>
    public string FullPath
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