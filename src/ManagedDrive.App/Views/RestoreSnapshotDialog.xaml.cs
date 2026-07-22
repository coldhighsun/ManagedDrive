using ManagedDrive.Cli.Core;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="RestoreSnapshotDialog"/>. Lets the user pick a previously
/// saved timestamped snapshot to restore onto a mounted disk.
/// </summary>
public partial class RestoreSnapshotDialog
{
    private readonly string _mainImagePath;
    private readonly DiskViewModel _target;

    /// <summary>
    /// Initializes the dialog listing <paramref name="snapshots"/> (newest first) available
    /// for <paramref name="target"/>.
    /// </summary>
    /// <param name="target">The disk the chosen snapshot will be restored onto.</param>
    /// <param name="snapshots">The disk's available snapshots.</param>
    public RestoreSnapshotDialog(DiskViewModel target, IReadOnlyList<SnapshotManager.SnapshotInfo> snapshots)
    {
        InitializeComponent();

        _target = target;
        _mainImagePath = target.Disk.Options.PersistImagePath!;
        SourceDescriptionText.Text = Loc.Format("RestoreSnapshot.Description", target.MountPoint, target.VolumeLabel);

        RefreshSnapshotList(snapshots);
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

    private async void Delete_Click(object sender, RoutedEventArgs e)
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

        var confirm = new ConfirmDialog(
            Loc.Get("Msg.DeleteSnapshotConfirmTitle"),
            Loc.Format("Msg.DeleteSnapshotConfirmBody", item.Display))
        {
            Owner = this
        };
        if (confirm.ShowDialog() != true)
        {
            return;
        }

        ViewChangesButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        OkButton.IsEnabled = false;
        try
        {
            await Task.Run(() => SnapshotManager.DeleteSnapshot(_mainImagePath, item.Path));
            RefreshSnapshotList(SnapshotManager.ListSnapshots(_mainImagePath));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                Loc.Get("Val.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            ViewChangesButton.IsEnabled = true;
            DeleteButton.IsEnabled = true;
            OkButton.IsEnabled = true;
        }
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

    private void RefreshSnapshotList(IReadOnlyList<SnapshotManager.SnapshotInfo> snapshots)
    {
        var previouslySelectedPath = (SnapshotListBox.SelectedItem as SnapshotItem)?.Path;

        SnapshotListBox.Items.Clear();
        foreach (var snapshot in snapshots.OrderByDescending(s => s.TimestampUtc))
        {
            SnapshotListBox.Items.Add(new SnapshotItem(
                snapshot.Path,
                $"{snapshot.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ({ByteFormatter.Format((ulong)snapshot.SizeBytes)})"));
        }

        if (SnapshotListBox.Items.Count > 0)
        {
            var restoredIndex = previouslySelectedPath is null
                ? -1
                : SnapshotListBox.Items.Cast<SnapshotItem>().ToList().FindIndex(i => i.Path == previouslySelectedPath);
            SnapshotListBox.SelectedIndex = restoredIndex >= 0 ? restoredIndex : 0;
            SnapshotCountText.Text = Loc.Format("RestoreSnapshot.Count", SnapshotListBox.Items.Count);
            SnapshotCountText.Visibility = Visibility.Visible;
            NoSnapshotsText.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoSnapshotsText.Visibility = Visibility.Visible;
            SnapshotCountText.Visibility = Visibility.Collapsed;
        }
    }

    private sealed record SnapshotItem(string Path, string Display)
    {
        public override string ToString() => Display;
    }

    private async void ViewChanges_Click(object sender, RoutedEventArgs e)
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

        ViewChangesButton.IsEnabled = false;
        try
        {
            var diff = await Task.Run(() => _target.Disk.DiffAgainstSnapshot(item.Path));

            var dialog = new SnapshotDiffDialog(item.Display, diff)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                Loc.Get("Val.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            ViewChangesButton.IsEnabled = true;
        }
    }
}