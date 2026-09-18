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
    private CancellationTokenSource? _busyCts;
    private string _filterText = string.Empty;
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
        Closing += (_, _) => _busyCts?.Cancel();

        var nodes = target.Disk.GetAllNodes();
        var root = BuildTree(nodes);
        _rootNodes = root.Children.Values.Select(child => ToNode(child, "\\" + child.Name)).ToList();

        UpdateSummaryText();

        if (_rootNodes.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            ContentList.Visibility = Visibility.Collapsed;
            FilterBox.IsEnabled = false;
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
                    child = new(segment, isDirectory: true);
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
    /// Recursively collects every node under <paramref name="nodes"/> (files and directories
    /// alike) whose <see cref="DiskContentNode.Name"/> matches <paramref name="pattern"/>, into
    /// <paramref name="results"/>. Used by <see cref="RebuildRows"/> to flatten the tree into a
    /// single filtered list, since a match nested several levels deep would otherwise be hidden
    /// by its collapsed ancestors.
    /// </summary>
    private static void CollectMatching(IEnumerable<DiskContentNode> nodes, string pattern, List<DiskContentNode> results)
    {
        foreach (var node in nodes)
        {
            if (WildcardMatcher.Matches(pattern, node.Name))
            {
                results.Add(node);
            }

            CollectMatching(node.Children, pattern, results);
        }
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
    /// <see cref="BusyOverlay"/>, since a recursive directory delete of many files can take a
    /// noticeable amount of time.
    /// </summary>
    private async void DeleteNode_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly)
        {
            return;
        }

        var nodesToDelete = GetSelectedNodesOrFallback(sender);
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
            new Progress<(int Completed, int Total)>(p => UpdateBusyProgressText(Loc.Get("DiskContent.Deleting"), p.Completed, p.Total));
        var token = ShowBusyOverlay(Loc.Get("DiskContent.Deleting"));

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
            HideBusyOverlay();
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

    /// <summary>
    /// Re-filters the content list as the user types, wrapping the entered text in <c>*...*</c>
    /// wildcards (unless it already contains <c>*</c>/<c>?</c>) so it behaves as a substring
    /// search rather than requiring a whole-name match.
    /// </summary>
    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterText = FilterBox.Text.Trim();
        RebuildRows();
    }

    private void ExpanderButton_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is DiskContentRow { CanExpand: true } row)
        {
            ToggleExpanded(row);
        }
    }

    /// <summary>
    /// Exports every selected row's node (or the right-clicked row when nothing is selected) to a
    /// user-chosen local folder, copying each file/directory from its real filesystem path via
    /// <see cref="ToRealPath"/> — the same path the WinFsp mount itself serves reads from, so this
    /// is a plain file copy rather than anything RAM-disk-specific. Runs off the UI thread behind
    /// <see cref="BusyOverlay"/>, same as <see cref="DeleteNode_Click"/>.
    /// </summary>
    private async void ExportNode_Click(object sender, RoutedEventArgs e)
    {
        var nodesToExport = GetSelectedNodesOrFallback(sender);
        if (nodesToExport.Count == 0)
        {
            return;
        }

        var dlg = new OpenFolderDialog
        {
            Title = Loc.Get("DiskContent.Export"),
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var destinationRoot = dlg.FolderName;

        IProgress<(int Completed, int Total)> progress =
            new Progress<(int Completed, int Total)>(p => UpdateBusyProgressText(Loc.Get("DiskContent.Exporting"), p.Completed, p.Total));
        var token = ShowBusyOverlay(Loc.Get("DiskContent.Exporting"));

        (string Name, string Message)? failure = null;

        try
        {
            await Task.Run(() =>
            {
                var totalFiles = nodesToExport.Sum(node => node.IsDirectory ? CountFilesSafe(ToRealPath(node.FullPath)) : 1);
                var completed = 0;
                progress.Report((completed, totalFiles));

                foreach (var node in nodesToExport)
                {
                    token.ThrowIfCancellationRequested();

                    var sourcePath = ToRealPath(node.FullPath);
                    var destinationPath = Path.Combine(destinationRoot, node.Name);

                    try
                    {
                        if (node.IsDirectory)
                        {
                            Directory.CreateDirectory(destinationPath);
                            foreach (var sourceFilePath in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
                            {
                                token.ThrowIfCancellationRequested();
                                var relative = Path.GetRelativePath(sourcePath, sourceFilePath);
                                var destinationFilePath = Path.Combine(destinationPath, relative);
                                Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
                                File.Copy(sourceFilePath, destinationFilePath, overwrite: true);
                                completed++;
                                progress.Report((completed, totalFiles));
                            }
                        }
                        else
                        {
                            File.Copy(sourcePath, destinationPath, overwrite: true);
                            completed++;
                            progress.Report((completed, totalFiles));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failure = (node.Name, ex.Message);
                        break;
                    }
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            HideBusyOverlay();
        }

        if (failure is { } f)
        {
            new ConfirmDialog(Loc.Get("DiskContent.Export"), Loc.Format("Msg.ExportNodeFailed", f.Name, f.Message))
            {
                Owner = this,
            }.ShowDialog();
        }
    }

    /// <summary>
    /// Resolves the node set a delete/export action should act on: the current multi-selection,
    /// or (when nothing is selected, e.g. a bare right-click) just the row the context menu was
    /// opened on. Either way, descendants of another selected directory are dropped so a
    /// recursive operation on the ancestor isn't repeated on its own children.
    /// </summary>
    private List<DiskContentNode> GetSelectedNodesOrFallback(object menuItemSender)
    {
        var selectedNodes = ContentList.SelectedItems.Cast<DiskContentRow>().Select(r => r.Node).ToList();
        if (selectedNodes.Count == 0)
        {
            if (GetRowFromMenuItem(menuItemSender) is not { } fallbackRow)
            {
                return [];
            }

            selectedNodes = [fallbackRow.Node];
        }

        return ExcludeDescendantsOfSelectedDirectories(selectedNodes);
    }

    /// <summary>
    /// Hides <see cref="BusyOverlay"/>, stops its spinner animation, disposes/clears
    /// <see cref="_busyCts"/>, and re-enables the content list.
    /// </summary>
    private void HideBusyOverlay()
    {
        BusySpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        BusyOverlay.Visibility = Visibility.Collapsed;
        ContentList.IsEnabled = true;
        _busyCts?.Dispose();
        _busyCts = null;
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
    /// Rebuilds <see cref="_rows"/> from <see cref="_rootNodes"/> in their current sort order.
    /// With no active filter, descends into a node's children only while that node is present in
    /// <see cref="_expandedNodes"/> (the normal tree view). With an active filter, ignores
    /// expansion state entirely and instead shows a flat list of every matching node anywhere in
    /// the tree, each labeled with its full path since the surrounding hierarchy is no longer
    /// visible to give it context.
    /// </summary>
    private void RebuildRows()
    {
        _rows.Clear();

        if (string.IsNullOrEmpty(_filterText))
        {
            AddRows(_rootNodes, depth: 0);
        }
        else
        {
            var pattern = _filterText.Contains('*') || _filterText.Contains('?') ? _filterText : $"*{_filterText}*";
            var matches = new List<DiskContentNode>();
            CollectMatching(_rootNodes, pattern, matches);
            matches.Sort(BuildComparer(_sortKey, _sortAscending));

            foreach (var node in matches)
            {
                _rows.Add(new(node, depth: 0, showFullPath: true));
            }
        }

        NoMatchesText.Visibility = _rootNodes.Count > 0 && _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ContentList.Visibility = NoMatchesText.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
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
        if (((ListViewItem)sender).Content is not DiskContentRow row || !row.CanExpand)
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
    /// Shows <see cref="BusyOverlay"/> over the content list with <paramref name="initialText"/>
    /// and starts its spinner spinning, disabling the list so the selection can't change
    /// mid-operation. Creates and returns the <see cref="CancellationToken"/> for the caller's
    /// background work (stored in <see cref="_busyCts"/> so the dialog's <c>Closing</c> handler
    /// can cancel an in-flight delete/export instead of leaving it running after the dialog is
    /// gone).
    /// </summary>
    private CancellationToken ShowBusyOverlay(string initialText)
    {
        ContentList.IsEnabled = false;
        BusyProgressText.Text = initialText;
        BusyOverlay.Visibility = Visibility.Visible;
        BusySpinnerRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });

        _busyCts = new();
        return _busyCts.Token;
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
    /// Updates the busy overlay's status text with a "x / total" file count, or falls back to
    /// <paramref name="fallbackText"/> when <paramref name="total"/> is unknown/zero (e.g. the
    /// selection is only empty directories, or the up-front file count failed).
    /// </summary>
    private void UpdateBusyProgressText(string fallbackText, int completed, int total) =>
        BusyProgressText.Text = total > 0
            ? Loc.Format("DiskContent.OperationProgress", fallbackText, completed, total)
            : fallbackText;

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
public sealed class DiskContentRow(DiskContentNode node, int depth, bool showFullPath = false) : INotifyPropertyChanged
{
    private bool _isExpanded;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets this row's nesting depth (0 for top-level nodes), used to indent the Name column.
    /// Always 0 for a filtered row (see <see cref="DisplayName"/>), since filtered results are
    /// shown as a flat list rather than nested under their ancestors.
    /// </summary>
    public int Depth { get; } = depth;

    /// <summary>
    /// Gets the text shown in the Name column: <see cref="DiskContentNode.Name"/> normally, or
    /// <see cref="DiskContentNode.FullPath"/> (without the leading <c>\</c>) for a filtered row,
    /// since its surrounding folder hierarchy is no longer visible to give it context.
    /// </summary>
    public string DisplayName => showFullPath ? Node.FullPath.TrimStart('\\') : Node.Name;

    /// <summary>
    /// Gets whether this row can be expanded/collapsed. Always <see langword="false"/> for a
    /// filtered row: the flat filtered list has no nested children to reveal, and toggling one
    /// would incorrectly fall back to the unfiltered tree view (see <see cref="ToggleExpanded"/>).
    /// </summary>
    public bool CanExpand => HasChildren && !showFullPath;

    /// <summary>
    /// Gets whether <see cref="Node"/> has any children.
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
