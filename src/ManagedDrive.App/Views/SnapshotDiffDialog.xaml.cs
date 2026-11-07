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
    private const double RowHeight = 32;
    private const double MinListHeight = 96;
    private const double MaxListHeight = 300;

    private readonly IReadOnlyList<DiffRow> _rows;

    /// <summary>
    /// Initializes the dialog showing <paramref name="diff"/>, the comparison result for the
    /// snapshot labeled <paramref name="snapshotLabel"/>.
    /// </summary>
    public SnapshotDiffDialog(string snapshotLabel, SnapshotManager.SnapshotDiffResult diff)
    {
        InitializeComponent();

        SourceDescriptionText.Text = Loc.Format("SnapshotDiff.Description", snapshotLabel);

        // In the snapshot but not on the disk now -> comes back; on the disk now but not in the
        // snapshot -> gets deleted; in both with other content -> reverted.
        var rows = new List<DiffRow>();
        rows.AddRange(ToRows(DiffKind.Restored, diff.RemovedDirectories, isDirectory: true));
        rows.AddRange(ToRows(DiffKind.Restored, diff.RemovedFiles, isDirectory: false));
        rows.AddRange(ToRows(DiffKind.Reverted, diff.ModifiedFiles, isDirectory: false));
        rows.AddRange(ToRows(DiffKind.Removed, diff.AddedDirectories, isDirectory: true));
        rows.AddRange(ToRows(DiffKind.Removed, diff.AddedFiles, isDirectory: false));
        _rows = rows;

        var restored = rows.Count(r => r.Kind == DiffKind.Restored);
        var removed = rows.Count(r => r.Kind == DiffKind.Removed);
        var reverted = rows.Count(r => r.Kind == DiffKind.Reverted);

        AllCountText.Text = rows.Count.ToString();
        RestoredCountText.Text = restored.ToString();
        RemovedCountText.Text = removed.ToString();
        RevertedCountText.Text = reverted.ToString();
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

        if (rows.Count == 0)
        {
            CardsPanel.Visibility = Visibility.Collapsed;
            ChangesList.Visibility = Visibility.Collapsed;
            NoChangesPanel.Visibility = Visibility.Visible;
            return;
        }

        // Fixed from the full set so the window does not change height while filtering.
        ChangesList.Height = Math.Clamp(rows.Count * RowHeight + 8, MinListHeight, MaxListHeight);
        AllCard.IsChecked = true;
    }

    private static IEnumerable<DiffRow> ToRows(DiffKind kind, IEnumerable<string> paths, bool isDirectory) =>
        paths.Order(StringComparer.OrdinalIgnoreCase).Select(p => DiffRow.Create(kind, p, isDirectory));

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        // Raised once from InitializeComponent-time wiring before the rows exist.
        if (_rows is null)
        {
            return;
        }

        ChangesList.ItemsSource = sender switch
        {
            _ when ReferenceEquals(sender, RestoredCard) => _rows.Where(r => r.Kind == DiffKind.Restored).ToList(),
            _ when ReferenceEquals(sender, RemovedCard) => _rows.Where(r => r.Kind == DiffKind.Removed).ToList(),
            _ when ReferenceEquals(sender, RevertedCard) => _rows.Where(r => r.Kind == DiffKind.Reverted).ToList(),
            _ => _rows
        };
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
/// One changed file or folder in <see cref="SnapshotDiffDialog"/>'s list.
/// </summary>
/// <param name="Kind">What restoring does to this path.</param>
/// <param name="Name">The last path segment.</param>
/// <param name="ParentPath">The containing folder (empty at the disk root).</param>
/// <param name="IsDirectory">Whether the path is a folder.</param>
/// <param name="FullPath">The full path, e.g. <c>\Sub\a.txt</c>.</param>
public sealed record DiffRow(DiffKind Kind, string Name, string ParentPath, bool IsDirectory, string FullPath)
{
    /// <summary>
    /// Gets the stroke geometry of the sign in the row's badge (plus, minus or tilde), in the
    /// badge's 20x20 coordinate space and centred on (10, 10).
    /// </summary>
    public string BadgeData => Kind switch
    {
        DiffKind.Restored => "M6,10 L14,10 M10,6 L10,14",
        DiffKind.Removed => "M6,10 L14,10",
        _ => "M5.5,11 C7.5,7.5 9,7.5 10,10 S12.5,12.5 14.5,9"
    };

    /// <summary>Gets the Segoe Fluent Icons glyph for a folder or a file.</summary>
    public string IconGlyph => IsDirectory ? "" : "";

    /// <summary>
    /// Builds a row from an absolute disk path such as <c>\Sub\a.txt</c>.
    /// </summary>
    public static DiffRow Create(DiffKind kind, string path, bool isDirectory)
    {
        var trimmed = path.TrimEnd('\\');
        var separator = trimmed.LastIndexOf('\\');
        var name = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
        var parent = separator > 0 ? trimmed[..separator] : string.Empty;
        return new(kind, name.Length == 0 ? path : name, parent, isDirectory, path);
    }
}
