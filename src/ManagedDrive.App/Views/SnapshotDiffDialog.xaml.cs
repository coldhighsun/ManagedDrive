namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="SnapshotDiffDialog"/>. Read-only view of the differences
/// between a snapshot and the disk's current live contents, shown before committing to a
/// restore.
/// </summary>
public partial class SnapshotDiffDialog
{
    /// <summary>
    /// Initializes the dialog showing <paramref name="diff"/>, the comparison result for the
    /// snapshot labeled <paramref name="snapshotLabel"/>.
    /// </summary>
    public SnapshotDiffDialog(string snapshotLabel, SnapshotManager.SnapshotDiffResult diff)
    {
        InitializeComponent();

        SourceDescriptionText.Text = Loc.Format("SnapshotDiff.Description", snapshotLabel);
        SummaryText.Text = Loc.Format(
            "SnapshotDiff.Summary",
            diff.AddedFiles.Count + diff.AddedDirectories.Count,
            diff.RemovedFiles.Count + diff.RemovedDirectories.Count,
            diff.ModifiedFiles.Count,
            diff.UnchangedFileCount);

        var groups = new List<DiffGroup>();
        AddGroup(groups, Loc.Get("SnapshotDiff.Added"), diff.AddedDirectories, diff.AddedFiles);
        AddGroup(groups, Loc.Get("SnapshotDiff.Removed"), diff.RemovedDirectories, diff.RemovedFiles);
        AddGroup(groups, Loc.Get("SnapshotDiff.Modified"), [], diff.ModifiedFiles);

        if (groups.Count == 0)
        {
            NoChangesText.Visibility = Visibility.Visible;
            ChangesTree.Visibility = Visibility.Collapsed;
        }
        else
        {
            ChangesTree.ItemsSource = groups;
        }
    }

    private static void AddGroup(List<DiffGroup> groups, string header, IReadOnlyList<string> directories, IReadOnlyList<string> files)
    {
        if (directories.Count == 0 && files.Count == 0)
        {
            return;
        }

        var allPaths = directories.Concat(files).ToList();
        groups.Add(new($"{header} ({allPaths.Count})", BuildTree(allPaths)));
    }

    /// <summary>
    /// Nests the given absolute paths (e.g. <c>\Sub\a.txt</c>) into a directory tree, so the
    /// display mirrors the disk's actual folder structure instead of a flat path list.
    /// Intermediate folders that weren't themselves changed are still shown, purely as
    /// grouping nodes.
    /// </summary>
    private static List<DiffNode> BuildTree(IEnumerable<string> paths)
    {
        var root = new SortedDictionary<string, TreeBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var level = root;
            foreach (var segment in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!level.TryGetValue(segment, out var node))
                {
                    node = new TreeBuilder(segment);
                    level[segment] = node;
                }

                level = node.Children;
            }
        }

        return root.Values.Select(ToNode).ToList();
    }

    private static DiffNode ToNode(TreeBuilder builder) =>
        new(builder.Name, builder.Children.Values.Select(ToNode).ToList());

    private sealed class TreeBuilder(string name)
    {
        public SortedDictionary<string, TreeBuilder> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Name { get; } = name;
    }
}

/// <summary>
/// A group of changed paths (Added/Removed/Modified) shown as an expandable root node in
/// <see cref="SnapshotDiffDialog"/>'s tree, with its display header and directory tree of
/// changed paths as children.
/// </summary>
public sealed record DiffGroup(string Header, IReadOnlyList<DiffNode> Nodes);

/// <summary>
/// One folder or file segment within a <see cref="DiffGroup"/>'s directory tree. A node with no
/// <see cref="Children"/> is a changed file (or an empty changed folder); a node with children
/// may itself be an unchanged intermediate folder used purely for grouping.
/// </summary>
public sealed record DiffNode(string Name, IReadOnlyList<DiffNode> Children);