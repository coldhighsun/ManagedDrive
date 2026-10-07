using ManagedDrive.Cli.Core;
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
    /// How many entries each list keeps.
    /// </summary>
    private const int TopCount = 200;

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
    /// Opens the dialog for <paramref name="target"/> and starts the analysis.
    /// </summary>
    /// <param name="target">The disk to analyse.</param>
    public SpaceUsageDialog(DiskViewModel target)
    {
        InitializeComponent();
        _target = target;

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
        Closing += (_, _) => _closing.Cancel();
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
        if (_analyzing)
        {
            return;
        }

        _analyzing = true;
        BusyOverlay.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        var reopen = Treemap.Directory?.Path;
        var disk = _target.Disk;
        var token = _closing.Token;

        try
        {
            var (report, root, categories) = await Task.Run(() =>
            {
                var report = SpaceUsageAnalyzer.Analyze(disk.GetAllNodes(), TopCount, out var root);
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
}
