using ManagedDrive.Cli.Core;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;

namespace ManagedDrive.App.Views;

/// <summary>
/// One line of a top list in <see cref="SpaceUsageDialog"/>.
/// </summary>
/// <param name="Name">What is listed: a path or an extension.</param>
/// <param name="Size">The memory it occupies, formatted.</param>
/// <param name="Share">Its share of the disk's used memory, formatted.</param>
/// <param name="Files">The number of files, formatted.</param>
/// <param name="Path">The path to open on double click, or <c>null</c> when there is none (extensions).</param>
public sealed record SpaceRow(string Name, string Size, string Share, string Files, string? Path);

/// <summary>
/// Interaction logic for <see cref="SpaceUsageDialog"/>. Shows what uses a mounted disk's memory
/// as a treemap (the main view) next to lists of the biggest folders, files and file types.
/// </summary>
public partial class SpaceUsageDialog
{
    /// <summary>
    /// Called with the new list size when the user changes it, so it can be remembered.
    /// </summary>
    private readonly Action<int> _listSizeChanged;

    /// <summary>
    /// The disk being analysed.
    /// </summary>
    private readonly DiskViewModel _target;

    /// <summary>
    /// Cancels an analysis still running when the dialog closes.
    /// </summary>
    private readonly CancellationTokenSource _closing = new();

    /// <summary>
    /// The root of the tree shown, or <c>null</c> before the first analysis completes.
    /// </summary>
    private SpaceNode? _root;

    /// <summary>
    /// The used bytes of the analysed disk, the base of the shares in the lists.
    /// </summary>
    private ulong _usedBytes;

    /// <summary>
    /// Whether an analysis is running, so a second one is not started.
    /// </summary>
    private bool _analyzing;

    /// <summary>
    /// How many entries the folder and file lists keep.
    /// </summary>
    private int _listSize;

    /// <summary>
    /// Whether a delete is running, so no second delete or analysis starts meanwhile.
    /// </summary>
    private bool _deleting;

    /// <summary>
    /// Whether another analysis was asked for while one was running, to be done when it finishes.
    /// </summary>
    private bool _rerunRequested;

    /// <summary>
    /// Whether the dialog is closing, after which nothing may start.
    /// </summary>
    private bool _closed;

    /// <summary>
    /// Opens the dialog for <paramref name="target"/> and starts the analysis.
    /// </summary>
    /// <param name="target">The disk to analyse.</param>
    /// <param name="listSize">How many entries the folder and file lists keep, as last chosen.</param>
    /// <param name="listSizeChanged">Called when the user picks another list size.</param>
    public SpaceUsageDialog(DiskViewModel target, int listSize, Action<int> listSizeChanged)
    {
        InitializeComponent();
        _target = target;
        _listSizeChanged = listSizeChanged;
        _listSize = SpaceUsageAnalyzer.ClampListSize(listSize);
        TopCountBox.Text = _listSize.ToString(CultureInfo.InvariantCulture);
        Treemap.CanDelete = !target.IsReadOnly;

        // Same as the disk-content dialog: the one other dialog that can be resized and maximized.
        WindowChrome.SetWindowChrome(this, new()
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new(6),
            GlassFrameThickness = new(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
        });
        WindowMaximizeHelper.HookMaximizeBehavior(this);

        CloseOnDisposing(target);
        Closing += (_, _) =>
        {
            _closed = true;
            _closing.Cancel();
        };
        Closed += (_, _) => _closing.Dispose();
        Loaded += async (_, _) => await AnalyzeAsync();
        StatusText.Text = Loc.Get("SpaceUsage.Hint");
    }

    /// <summary>
    /// Reads the disk and fills the map, the lists and the legend. Keeps the folder being looked at
    /// when run again.
    /// </summary>
    /// <returns>A task completing when the view is updated.</returns>
    private async Task AnalyzeAsync()
    {
        if (_closed)
        {
            return;
        }

        if (_analyzing || _deleting)
        {
            _rerunRequested = true;
            return;
        }

        _rerunRequested = false;
        _analyzing = true;
        BusyText.Text = Loc.Get("SpaceUsage.Analyzing");
        BusyOverlay.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        var reopen = Treemap.Directory?.Path;
        var disk = _target.Disk;
        var size = _listSize;
        var token = _closing.Token;

        try
        {
            var (report, root, categories) = await Task.Run(() =>
            {
                var report = SpaceUsageAnalyzer.Analyze(disk.GetAllNodes(), size, out var root);
                return (report, root, SumCategories(root));
            }, token);

            _root = root;
            _usedBytes = report.TotalAllocated;
            Treemap.SetTree(root);
            if (reopen is not null && FindNode(root, reopen) is { IsDirectory: true } previous)
            {
                Treemap.Open(previous);
            }

            SummaryText.Text = Loc.Format(
                "SpaceUsage.Summary",
                ByteFormatter.Format(report.TotalAllocated),
                ByteFormatter.Format(disk.TotalBytes),
                report.FileCount,
                report.DirectoryCount,
                ByteFormatter.Format(report.TotalLogical));
            FolderList.ItemsSource = report.TopDirectories.Select(ToRow).ToList();
            FileList.ItemsSource = report.TopFiles.Select(ToRow).ToList();
            ExtensionList.ItemsSource = report.TopExtensions.Select(extension => new SpaceRow(
                extension.Extension,
                ByteFormatter.Format(extension.Allocated),
                Percent(extension.Allocated),
                extension.FileCount.ToString("N0"),
                null)).ToList();
            FillLegend(categories);
            EmptyText.Visibility = report.FileCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // The dialog is closing.
        }
        catch (ObjectDisposedException)
        {
            // The disk was unmounted while analysing; the dialog closes itself.
        }
        catch (Exception ex)
        {
            SummaryText.Text = Loc.Format("SpaceUsage.Failed", ex.Message);
        }
        finally
        {
            _analyzing = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
        }

        if (_rerunRequested)
        {
            await AnalyzeAsync();
        }
    }

    /// <summary>
    /// Totals the memory of all files per category.
    /// </summary>
    /// <param name="root">The tree to walk.</param>
    /// <returns>Bytes per category, for the categories that occur.</returns>
    private static Dictionary<FileCategory, ulong> SumCategories(SpaceNode root)
    {
        var totals = new Dictionary<FileCategory, ulong>();
        var pending = new Stack<SpaceNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var child in pending.Pop().Children)
            {
                if (child.IsDirectory)
                {
                    pending.Push(child);
                }
                else if (!child.IsLink)
                {
                    var category = FileCategories.Of(child.Name);
                    totals[category] = totals.GetValueOrDefault(category) + child.Allocated;
                }
            }
        }

        return totals;
    }

    /// <summary>
    /// Rebuilds the colour key under the map.
    /// </summary>
    /// <param name="totals">Bytes per category.</param>
    private void FillLegend(Dictionary<FileCategory, ulong> totals)
    {
        LegendPanel.Children.Clear();
        foreach (var (category, bytes) in totals.OrderByDescending(pair => pair.Value))
        {
            var swatch = new Border { Width = 10, Height = 10, Margin = new(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            swatch.SetResourceReference(Border.BackgroundProperty, $"AppSpace{category}");

            var label = new TextBlock
            {
                Text = $"{Loc.Get($"SpaceUsage.Category.{category}")} {ByteFormatter.Format(bytes)}",
                FontSize = 11,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "AppForegroundLight");

            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 14, 2) };
            item.Children.Add(swatch);
            item.Children.Add(label);
            LegendPanel.Children.Add(item);
        }
    }

    /// <summary>
    /// Turns a top-list entry into a list row.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The row.</returns>
    private SpaceRow ToRow(SpaceUsageEntry entry) =>
        new(entry.Path, ByteFormatter.Format(entry.Allocated), Percent(entry.Allocated), entry.FileCount.ToString("N0"), entry.Path);

    /// <summary>
    /// Formats a share of the disk's used memory.
    /// </summary>
    /// <param name="bytes">The part, in bytes.</param>
    /// <returns>For example <c>12.3%</c>.</returns>
    private string Percent(ulong bytes) =>
        _usedBytes == 0 ? "0%" : $"{(double)bytes / _usedBytes * 100.0:0.#}%";

    /// <summary>
    /// Finds the node at a path.
    /// </summary>
    /// <param name="root">The root of the tree.</param>
    /// <param name="path">A disk path such as <c>\Folder\a.txt</c>.</param>
    /// <returns>The node, or <c>null</c> if the path does not exist.</returns>
    private static SpaceNode? FindNode(SpaceNode root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Children.FirstOrDefault(child => string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>
    /// Lets only digits into the list-size box.
    /// </summary>
    /// <param name="sender">The box.</param>
    /// <param name="e">The typed text.</param>
    private void TopCountBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsAsciiDigit);

    /// <summary>
    /// Applies the list size when Enter is pressed.
    /// </summary>
    /// <param name="sender">The box.</param>
    /// <param name="e">The key.</param>
    private async void TopCountBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ApplyListSizeAsync();
        }
    }

    /// <summary>
    /// Applies the list size when the box loses focus.
    /// </summary>
    /// <param name="sender">The box.</param>
    /// <param name="e">Unused.</param>
    private async void TopCountBox_LostFocus(object sender, RoutedEventArgs e) => await ApplyListSizeAsync();

    /// <summary>
    /// Reads the list-size box, limits it to the allowed range, shows the result and, if it differs
    /// from the current size, remembers it and analyses again.
    /// </summary>
    /// <returns>A task completing when the view is updated.</returns>
    private async Task ApplyListSizeAsync()
    {
        var size = int.TryParse(TopCountBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var typed)
            ? SpaceUsageAnalyzer.ClampListSize(typed)
            : _listSize;
        TopCountBox.Text = size.ToString(CultureInfo.InvariantCulture);
        if (size == _listSize)
        {
            return;
        }

        _listSize = size;
        _listSizeChanged(size);
        await AnalyzeAsync();
    }

    /// <summary>
    /// Runs the analysis again.
    /// </summary>
    /// <param name="sender">The refresh button.</param>
    /// <param name="e">Unused.</param>
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    /// <summary>
    /// Goes to the parent folder.
    /// </summary>
    /// <param name="sender">The up button.</param>
    /// <param name="e">Unused.</param>
    private void UpButton_Click(object sender, RoutedEventArgs e) => Treemap.GoUp();

    /// <summary>
    /// Updates the path and the up button after the map changed folder.
    /// </summary>
    /// <param name="sender">The map.</param>
    /// <param name="e">Unused.</param>
    private void Treemap_DirectoryChanged(object? sender, EventArgs e)
    {
        PathText.Text = Treemap.Directory?.Path ?? string.Empty;
        UpButton.IsEnabled = Treemap.CanGoUp;
    }

    /// <summary>
    /// Shows the node chosen in the map's context menu in Windows Explorer.
    /// </summary>
    /// <param name="sender">The map.</param>
    /// <param name="node">The folder or file.</param>
    private void Treemap_OpenInExplorerRequested(object? sender, SpaceNode node) => OpenInExplorer(node);

    /// <summary>
    /// Deletes the node chosen in the map's context menu.
    /// </summary>
    /// <param name="sender">The map.</param>
    /// <param name="node">The folder or file.</param>
    private async void Treemap_DeleteRequested(object? sender, SpaceNode node) => await DeleteNodeAsync(node);

    /// <summary>
    /// Shows the selected item in the status line.
    /// </summary>
    /// <param name="sender">The map.</param>
    /// <param name="e">Unused.</param>
    private void Treemap_SelectionChanged(object? sender, EventArgs e) =>
        StatusText.Text = Treemap.SelectedNode is { } node
            ? Loc.Format("SpaceUsage.Selected", node.Path, ByteFormatter.Format(node.Allocated), Percent(node.Allocated))
            : Loc.Get("SpaceUsage.Hint");

    /// <summary>
    /// Shows a folder or file of a list in the map: a folder is opened, a file is shown selected
    /// inside its folder.
    /// </summary>
    /// <param name="sender">The list.</param>
    /// <param name="e">Unused.</param>
    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var list = (ListView)sender;
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(list, source) is not ListViewItem)
        {
            return;
        }

        if (_root is null || list.SelectedItem is not SpaceRow { Path: { } path } || FindNode(_root, path) is not { } node)
        {
            return;
        }

        ShowInMap(node);
    }

    /// <summary>
    /// Shows a node in the map: a folder is opened, a file is shown selected inside its folder.
    /// </summary>
    /// <param name="node">The folder or file.</param>
    private void ShowInMap(SpaceNode node)
    {
        if (node.IsDirectory)
        {
            Treemap.Open(node);
        }
        else if (node.Parent is { } parent)
        {
            Treemap.Open(parent);
            Treemap.Select(node);
        }
    }

    /// <summary>
    /// Converts a node's disk path into the real path under the disk's mount point.
    /// </summary>
    /// <param name="node">The folder or file.</param>
    /// <returns>The path as Explorer and the file system see it.</returns>
    private string ToRealPath(SpaceNode node) =>
        Path.Combine(_target.Disk.MountPoint.TrimEnd('\\') + '\\', node.Path.TrimStart('\\').Replace('\\', Path.DirectorySeparatorChar));

    /// <summary>
    /// Shows a folder in Windows Explorer, or the folder holding a file with the file selected.
    /// </summary>
    /// <param name="node">The folder or file.</param>
    private void OpenInExplorer(SpaceNode node)
    {
        var path = ToRealPath(node);
        var info = new System.Diagnostics.ProcessStartInfo("explorer.exe")
        {
            Arguments = node.IsDirectory ? $"\"{path}\"" : $"/select,\"{path}\"",
            UseShellExecute = true,
        };

        try
        {
            using var process = System.Diagnostics.Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SummaryText.Text = Loc.Format("SpaceUsage.Failed", ex.Message);
        }
    }

    /// <summary>
    /// Selects the row under the mouse and offers to show or delete its folder or file.
    /// </summary>
    /// <param name="sender">The folder or file list.</param>
    /// <param name="e">The mouse event.</param>
    private void List_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var list = (ListView)sender;
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(list, source) is not ListViewItem item
            || item.Content is not SpaceRow { Path: { } path }
            || _root is null
            || FindNode(_root, path) is not { } node)
        {
            return;
        }

        item.IsSelected = true;

        var open = new MenuItem { Header = Loc.Get(node.IsDirectory ? "SpaceUsage.OpenFolder" : "SpaceUsage.OpenContainingFolder") };
        open.Click += (_, _) => OpenInExplorer(node);

        var delete = new MenuItem
        {
            Header = Loc.Get(node.IsDirectory ? "SpaceUsage.DeleteFolder" : "SpaceUsage.DeleteFile"),
            IsEnabled = !_target.IsReadOnly && node.Parent is not null,
        };
        delete.Click += async (_, _) => await DeleteNodeAsync(node);

        var menu = new ContextMenu { PlacementTarget = list };
        menu.Items.Add(open);
        menu.Items.Add(delete);
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// Deletes a folder (with everything in it) or a file from the mounted disk after a
    /// confirmation, going through the real file system so the driver does the accounting, then
    /// analyses the disk again.
    /// </summary>
    /// <param name="node">The folder or file to delete.</param>
    /// <returns>A task completing when the view is updated.</returns>
    private async Task DeleteNodeAsync(SpaceNode node)
    {
        if (_closed || _analyzing || _deleting || _target.IsReadOnly || node.Parent is null)
        {
            return;
        }

        var body = Loc.Format(node.IsDirectory ? "Msg.DeleteNodeConfirmBodyFolder" : "Msg.DeleteNodeConfirmBodyFile", node.Name);
        if (new ConfirmDialog(Loc.Get("Msg.DeleteNodeConfirmTitle"), body) { Owner = this }.ShowDialog() != true)
        {
            return;
        }

        var realPath = ToRealPath(node);
        var isDirectory = node.IsDirectory;
        string? failure = null;

        _deleting = true;
        RefreshButton.IsEnabled = false;
        BusyText.Text = Loc.Get("DiskContent.Deleting");
        BusyOverlay.Visibility = Visibility.Visible;
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    if (isDirectory)
                    {
                        Directory.Delete(realPath, recursive: true);
                    }
                    else
                    {
                        File.Delete(realPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure = ex.Message;
                }
            });
        }
        finally
        {
            _deleting = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
        }

        if (_closed)
        {
            return;
        }

        if (failure is not null)
        {
            new ConfirmDialog(Loc.Get("Msg.DeleteNodeConfirmTitle"), Loc.Format("Msg.DeleteNodeFailed", node.Name, failure)) { Owner = this }.ShowDialog();
        }

        await AnalyzeAsync();
    }
}
