using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="SnapshotDiffDialog"/>. Read-only view of what restoring a
/// snapshot would change on the disk, shown before committing to the restore. The changes are
/// described from the restore's point of view (what comes back, what is removed, what is
/// reverted), which is the reverse of how <see cref="SnapshotManager.DiffAgainstCurrent"/>
/// names them (it compares the snapshot against the live disk).
/// </summary>
public partial class SnapshotDiffDialog
{
    /// <summary>
    /// The top-level nodes of the folder tree (all collapsed at first); <see langword="null"/>
    /// until the comparison has finished.
    /// </summary>
    private IReadOnlyList<DiffTreeNode>? _roots;

    /// <summary>
    /// The kind the selected filter card shows, or <see langword="null"/> for all.
    /// </summary>
    private DiffKind? _filter;

    /// <summary>
    /// The comparison that is still running when the dialog opens.
    /// </summary>
    private readonly Task<SnapshotManager.SnapshotDiffResult> _diffTask;

    /// <summary>
    /// Whether the dialog has been closed, so a late result is dropped.
    /// </summary>
    private bool _closed;

    /// <summary>
    /// Initializes the dialog for the snapshot labeled <paramref name="snapshotLabel"/>. It opens
    /// at once with a loading animation and shows the result when <paramref name="diffTask"/>
    /// completes; if that fails, the error is shown and the dialog closes.
    /// </summary>
    public SnapshotDiffDialog(string snapshotLabel, Task<SnapshotManager.SnapshotDiffResult> diffTask)
    {
        _diffTask = diffTask;
        InitializeComponent();
        SourceDescriptionText.Text = Loc.Format("SnapshotDiff.Description", snapshotLabel);

        // Same chrome as the other resizable dialogs: wider resize border, and a maximized window
        // that stays inside the work area.
        WindowChrome.SetWindowChrome(this, new()
        {
            CaptionHeight = 48,
            ResizeBorderThickness = new(6),
            GlassFrameThickness = new(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
        });
        WindowMaximizeHelper.HookMaximizeBehavior(this);

        SpinnerRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });

        Closed += (_, _) => _closed = true;
        Loaded += async (_, _) => await ShowResultAsync();
    }

    /// <summary>
    /// Waits for the comparison and shows it, or reports why it failed.
    /// </summary>
    private async Task ShowResultAsync()
    {
        try
        {
            var diff = await _diffTask;
            if (_closed)
            {
                return;
            }

            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            LoadingPanel.Visibility = Visibility.Collapsed;
            ApplyDiff(diff);
        }
        catch (Exception ex)
        {
            // Covers the comparison itself and filling the dialog from its result: either way
            // report it here and close, rather than leave an empty dialog behind.
            if (!_closed)
            {
                AppMessageBox.Show(ex.Message, Loc.Get("Val.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
        }
    }

    /// <summary>
    /// Fills the dialog with the comparison result.
    /// </summary>
    /// <param name="diff">The comparison of the snapshot with the disk.</param>
    private void ApplyDiff(SnapshotManager.SnapshotDiffResult diff)
    {
        // In the snapshot but not on the disk now -> comes back; on the disk now but not in the
        // snapshot -> gets deleted; in both with other content -> reverted.
        var rows = new List<DiffRow>();
        rows.AddRange(ToRows(DiffKind.Restored, diff.RemovedDirectories, isDirectory: true));
        rows.AddRange(ToRows(DiffKind.Restored, diff.RemovedFiles, isDirectory: false));
        rows.AddRange(ToRows(DiffKind.Reverted, diff.ModifiedFiles, isDirectory: false));
        rows.AddRange(ToRows(DiffKind.Removed, diff.AddedDirectories, isDirectory: true));
        rows.AddRange(ToRows(DiffKind.Removed, diff.AddedFiles, isDirectory: false));
        _roots = DiffTreeBuilder.Build(rows);

        var restored = rows.Count(r => r.Kind == DiffKind.Restored);
        var removed = rows.Count(r => r.Kind == DiffKind.Removed);
        var reverted = rows.Count(r => r.Kind == DiffKind.Reverted);

        // The cards count files, like the "(n)" after each folder in the tree; a changed folder is
        // not a file, so it is not counted (it still enables the card of its kind, above).
        AllCountText.Text = rows.Count(r => !r.IsDirectory).ToString();
        RestoredCountText.Text = rows.Count(r => !r.IsDirectory && r.Kind == DiffKind.Restored).ToString();
        RemovedCountText.Text = rows.Count(r => !r.IsDirectory && r.Kind == DiffKind.Removed).ToString();
        RevertedCountText.Text = rows.Count(r => !r.IsDirectory && r.Kind == DiffKind.Reverted).ToString();
        RestoredCard.IsEnabled = restored > 0;
        RemovedCard.IsEnabled = removed > 0;
        RevertedCard.IsEnabled = reverted > 0;

        if (diff.UnchangedFileCount > 0)
        {
            UnchangedText.Text = Loc.Format("SnapshotDiff.Unchanged", diff.UnchangedFileCount);
        }
        else
        {
            UnchangedText.Visibility = Visibility.Collapsed;
        }

        ResultBorder.Visibility = Visibility.Visible;
        if (rows.Count == 0)
        {
            ChangesList.Visibility = Visibility.Collapsed;
            NoChangesPanel.Visibility = Visibility.Visible;
            return;
        }

        CardsPanel.Visibility = Visibility.Visible;
        AllCard.IsChecked = true;
    }

    /// <summary>
    /// Turns paths of one kind into rows, sorted by path.
    /// </summary>
    /// <param name="kind">What restoring does to these paths.</param>
    /// <param name="paths">The absolute paths.</param>
    /// <param name="isDirectory">Whether the paths are folders.</param>
    /// <returns>The rows.</returns>
    private static IEnumerable<DiffRow> ToRows(DiffKind kind, IEnumerable<string> paths, bool isDirectory) =>
        paths.Order(StringComparer.OrdinalIgnoreCase).Select(p => DiffRow.Create(kind, p, isDirectory));

    /// <summary>
    /// Applies the filter of the filter card that was selected and rebuilds the list.
    /// </summary>
    /// <param name="sender">The selected filter card.</param>
    /// <param name="e">The routed event data.</param>
    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        // Raised once from InitializeComponent-time wiring before the tree exists.
        if (_roots is null)
        {
            return;
        }

        _filter = sender switch
        {
            _ when ReferenceEquals(sender, RestoredCard) => DiffKind.Restored,
            _ when ReferenceEquals(sender, RemovedCard) => DiffKind.Removed,
            _ when ReferenceEquals(sender, RevertedCard) => DiffKind.Reverted,
            _ => null
        };

        // The expansion of each node is kept, so switching the filter keeps what was opened.
        Refresh();
    }

    /// <summary>
    /// Rebuilds the displayed list from the tree in one pass and hands it to the list box. Done
    /// as a whole instead of row by row, because every inserted or removed row would raise its own
    /// change notification and relayout, which freezes the dialog on a folder with many files.
    /// </summary>
    /// <param name="keepVisible">A node to select and scroll back into view afterwards, if any.</param>
    private void Refresh(DiffTreeNode? keepVisible = null)
    {
        ChangesList.ItemsSource = DiffTreeBuilder.Flatten(_roots!, _filter);
        if (keepVisible is null)
        {
            return;
        }

        // The new list starts at the top with nothing selected, and its rows are only generated
        // after layout, so select and scroll once that has run.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                ChangesList.SelectedItem = keepVisible;
                ChangesList.ScrollIntoView(keepVisible);
            });
    }

    /// <summary>
    /// Expands or collapses the folder whose expander button was clicked.
    /// </summary>
    /// <param name="sender">The list.</param>
    /// <param name="e">The click event; its source is the expander button.</param>
    private void Expander_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: DiffTreeNode { CanExpand: true } node })
        {
            ToggleExpanded(node);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Expands or collapses a folder row when it is double-clicked anywhere but on the expander.
    /// </summary>
    /// <param name="sender">The list.</param>
    /// <param name="e">The mouse event.</param>
    private void ChangesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<ListBoxItem>(source) is { DataContext: DiffTreeNode { CanExpand: true } node }
            && FindAncestor<Button>(source) is null)
        {
            ToggleExpanded(node);
        }
    }

    /// <summary>
    /// Shows or hides the descendants of <paramref name="node"/> and keeps the node in view.
    /// </summary>
    /// <param name="node">The folder to toggle.</param>
    private void ToggleExpanded(DiffTreeNode node)
    {
        node.IsExpanded = !node.IsExpanded;
        Refresh(node);
    }

    /// <summary>
    /// Finds the closest ancestor of <typeparamref name="T"/> of <paramref name="start"/>, itself included.
    /// </summary>
    /// <typeparam name="T">The ancestor type.</typeparam>
    /// <param name="start">Where to start, possibly a content element such as a run.</param>
    /// <returns>The ancestor, or <see langword="null"/> when there is none.</returns>
    private static T? FindAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        while (start is not null)
        {
            if (start is T match)
            {
                return match;
            }

            start = start is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(start)
                : LogicalTreeHelper.GetParent(start);
        }

        return null;
    }
}

/// <summary>
/// What restoring a snapshot would do to one path of the disk.
/// </summary>
public enum DiffKind
{
    /// <summary>In the snapshot but missing from the disk now: it reappears.</summary>
    Restored,

    /// <summary>On the disk now but not in the snapshot: it is deleted.</summary>
    Removed,

    /// <summary>In both with different content: it goes back to the snapshot's content.</summary>
    Reverted
}

/// <summary>
/// One changed file or folder, the input <see cref="DiffTreeBuilder"/> turns into the dialog's tree.
/// </summary>
/// <param name="Kind">What restoring does to this path.</param>
/// <param name="IsDirectory">Whether the path is a folder.</param>
/// <param name="FullPath">The full path, e.g. <c>\Sub\a.txt</c>.</param>
public sealed record DiffRow(DiffKind Kind, bool IsDirectory, string FullPath)
{
    /// <summary>
    /// Builds a row from an absolute disk path such as <c>\Sub\a.txt</c>.
    /// </summary>
    /// <param name="kind">What restoring does to the path.</param>
    /// <param name="path">The absolute path.</param>
    /// <param name="isDirectory">Whether the path is a folder.</param>
    /// <returns>The row.</returns>
    public static DiffRow Create(DiffKind kind, string path, bool isDirectory) => new(kind, isDirectory, path);
}
