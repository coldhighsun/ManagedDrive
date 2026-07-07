using ManagedDrive.App.Helpers;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="RestoreSnapshotDialog"/>. Lets the user pick a previously
/// saved timestamped snapshot to restore onto a mounted disk.
/// </summary>
public partial class RestoreSnapshotDialog
{
    /// <summary>
    /// Initializes the dialog listing <paramref name="snapshots"/> (newest first) available
    /// for <paramref name="target"/>.
    /// </summary>
    /// <param name="target">The disk the chosen snapshot will be restored onto.</param>
    /// <param name="snapshots">The disk's available snapshots.</param>
    public RestoreSnapshotDialog(DiskViewModel target, IReadOnlyList<SnapshotManager.SnapshotInfo> snapshots)
    {
        InitializeComponent();

        SourceDescriptionText.Text = Loc.Format("RestoreSnapshot.Description", target.MountPoint, target.VolumeLabel);

        foreach (var snapshot in snapshots.OrderByDescending(s => s.TimestampUtc))
        {
            SnapshotListBox.Items.Add(new SnapshotItem(
                snapshot.Path,
                $"{snapshot.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ({ByteFormatter.Format((ulong)snapshot.SizeBytes)})"));
        }

        if (SnapshotListBox.Items.Count > 0)
        {
            SnapshotListBox.SelectedIndex = 0;
            SnapshotCountText.Text = Loc.Format("RestoreSnapshot.Count", SnapshotListBox.Items.Count);
        }
        else
        {
            NoSnapshotsText.Visibility = Visibility.Visible;
            SnapshotCountText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Gets the display label of the snapshot the user selected; <c>null</c> until confirmed.
    /// </summary>
    public string? SelectedSnapshotLabel
    {
        get; private set;
    }

    /// <summary>
    /// Gets the path of the snapshot the user selected; <c>null</c> until confirmed.
    /// </summary>
    public string? SelectedSnapshotPath
    {
        get; private set;
    }

    private sealed record SnapshotItem(string Path, string Display)
    {
        public override string ToString() => Display;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotListBox.SelectedItem is not SnapshotItem item)
        {
            MessageBox.Show(
                Loc.Get("Val.NoSnapshotSelected"),
                Loc.Get("Val.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SelectedSnapshotPath = item.Path;
        SelectedSnapshotLabel = item.Display;
        DialogResult = true;
    }
}