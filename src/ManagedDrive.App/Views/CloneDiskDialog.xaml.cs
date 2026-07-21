using System.Windows.Input;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="CloneDiskDialog"/>. Lets the user copy a mounted disk's
/// contents either onto another mounted disk (overwriting its contents) or out to a new
/// standalone <c>.mdr</c> image file.
/// </summary>
public partial class CloneDiskDialog
{
    private static readonly List<ImageCompressionLevel> CompressionLevels =
    [
        ImageCompressionLevel.None,
        ImageCompressionLevel.Fastest,
        ImageCompressionLevel.Optimal,
        ImageCompressionLevel.SmallestSize,
    ];

    private static readonly List<ArchiveExportFormat?> ExportFormats =
    [
        null,
        ArchiveExportFormat.Zip,
        ArchiveExportFormat.SevenZip,
    ];

    private readonly IReadOnlyList<DiskOptions> _otherDisks;
    private readonly IReadOnlyList<DiskViewModel> _targets;

    /// <summary>
    /// Initializes the dialog for cloning <paramref name="source"/> into one of
    /// <paramref name="targets"/> or exporting it to a new image file.
    /// </summary>
    /// <param name="source">The disk whose contents will be copied.</param>
    /// <param name="targets">Other mounted, writable disks eligible as a clone destination.</param>
    /// <param name="otherDisks">
    /// Options of every other currently active disk (excluding <paramref name="source"/>), used
    /// to reject an export path that is already used as another disk's image file.
    /// </param>
    public CloneDiskDialog(DiskViewModel source, IReadOnlyList<DiskViewModel> targets, IReadOnlyList<DiskOptions> otherDisks)
    {
        InitializeComponent();
        _targets = targets;
        _otherDisks = otherDisks;

        SourceDescriptionText.Text = Loc.Format("CloneDisk.SourceDescription", source.MountPoint, source.VolumeLabel);

        foreach (var target in targets)
        {
            TargetDiskBox.Items.Add(new CloneTargetItem(target, $"{target.MountPoint} ({target.VolumeLabel})"));
        }

        foreach (var level in CompressionLevels)
        {
            CompressionLevelBox.Items.Add(new CompressionLevelItem(level, Loc.Get(CompressionLevelKey(level))));
        }
        CompressionLevelBox.SelectedIndex = CompressionLevels.IndexOf(ImageCompressionLevel.Fastest);

        foreach (var format in ExportFormats)
        {
            ExportFormatBox.Items.Add(new ExportFormatItem(format, Loc.Get(ExportFormatKey(format))));
        }
        ExportFormatBox.SelectedIndex = 0;
        ExportFormatBox.SelectionChanged += (_, _) => UpdateExportPathExtension();

        // Set the initial radio selection only after all named elements above are assigned —
        // setting IsChecked in XAML would fire the Checked handler mid-InitializeComponent,
        // before later-declared fields like TargetDiskBox exist, causing a NullReferenceException.
        if (targets.Count > 0)
        {
            TargetDiskBox.SelectedIndex = 0;
            CloneToDiskOption.IsChecked = true;
        }
        else
        {
            CloneToDiskOption.IsEnabled = false;
            ExportToFileOption.IsChecked = true;
        }

        UpdateModeState();
    }

    /// <summary>
    /// Gets the compression level to use, valid only when <see cref="ExportPath"/> is set.
    /// </summary>
    public ImageCompressionLevel ExportCompressionLevel
    {
        get; private set;
    }

    /// <summary>
    /// Gets the archive format to use when <see cref="ExportPath"/> targets a <c>.zip</c> or
    /// <c>.7z</c> file rather than a <c>.mdr</c> image; <c>null</c> for a <c>.mdr</c> export or
    /// when cloning to another mounted disk.
    /// </summary>
    public ArchiveExportFormat? ExportArchiveFormat
    {
        get; private set;
    }

    /// <summary>
    /// Gets the destination path when the user chose to export to a new image file;
    /// <c>null</c> when cloning to another mounted disk instead.
    /// </summary>
    public string? ExportPath
    {
        get; private set;
    }

    /// <summary>
    /// Gets the disk selected as the clone target when the user chose that mode;
    /// <c>null</c> when exporting to a file instead.
    /// </summary>
    public DiskViewModel? TargetDisk
    {
        get; private set;
    }

    private static string CompressionLevelKey(ImageCompressionLevel level) => level switch
    {
        ImageCompressionLevel.None => "CompressionLevel.None",
        ImageCompressionLevel.Fastest => "CompressionLevel.Fastest",
        ImageCompressionLevel.SmallestSize => "CompressionLevel.SmallestSize",
        _ => "CompressionLevel.Optimal",
    };

    private static string ExportFormatKey(ArchiveExportFormat? format) => format switch
    {
        ArchiveExportFormat.Zip => "CloneDisk.ExportFormat.Zip",
        ArchiveExportFormat.SevenZip => "CloneDisk.ExportFormat.SevenZip",
        _ => "CloneDisk.ExportFormat.Mdr",
    };

    private static string ExportExtension(ArchiveExportFormat? format) => format switch
    {
        ArchiveExportFormat.Zip => ".zip",
        ArchiveExportFormat.SevenZip => ".7z",
        _ => ".mdr",
    };

    private static string ExportFilterKey(ArchiveExportFormat? format) => format switch
    {
        ArchiveExportFormat.Zip => "SaveDlg.Filter.Zip",
        ArchiveExportFormat.SevenZip => "SaveDlg.Filter.SevenZip",
        _ => "SaveDlg.Filter",
    };

    private sealed record CompressionLevelItem(ImageCompressionLevel Level, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record ExportFormatItem(ArchiveExportFormat? Format, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record CloneTargetItem(DiskViewModel Disk, string Display)
    {
        public override string ToString() => Display;
    }

    private void ExportPathBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenExportPathDialog();
    }

    private void ModeOption_CheckedChanged(object sender, RoutedEventArgs e) => UpdateModeState();

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (CloneToDiskOption.IsChecked == true)
        {
            if (TargetDiskBox.SelectedItem is not CloneTargetItem target)
            {
                MessageBox.Show(
                    Loc.Get("Val.NoCloneTarget"),
                    Loc.Get("Val.Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            TargetDisk = target.Disk;
            ExportPath = null;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(ExportPathBox.Text))
            {
                MessageBox.Show(
                    Loc.Get("Val.NoExportPath"),
                    Loc.Get("Val.Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            TargetDisk = null;
            ExportPath = ExportPathBox.Text.Trim();
            ExportArchiveFormat = (ExportFormatBox.SelectedItem as ExportFormatItem)?.Format;
            ExportCompressionLevel = (CompressionLevelBox.SelectedItem as CompressionLevelItem)?.Level
                ?? ImageCompressionLevel.Fastest;
        }

        DialogResult = true;
    }

    private void OpenExportPathDialog()
    {
        var format = (ExportFormatBox.SelectedItem as ExportFormatItem)?.Format;
        var extension = ExportExtension(format);

        var dlg = new SaveFileDialog
        {
            Title = Loc.Get("SaveDlg.Title"),
            Filter = Loc.Get(ExportFilterKey(format)),
            DefaultExt = extension,
            OverwritePrompt = true,
            FileName = ExportPathBox.Text,
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        if (_otherDisks.Any(d => d.PersistImagePath != null &&
            string.Equals(d.PersistImagePath, dlg.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                Loc.Get("Val.ImagePathInUse"),
                Loc.Get("Val.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (SnapshotManager.IsSnapshotFileName(Path.GetFileName(dlg.FileName)))
        {
            MessageBox.Show(
                Loc.Get("Val.ImagePathIsSnapshot"),
                Loc.Get("Val.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ExportPathBox.Text = dlg.FileName;
    }

    private void UpdateExportPathExtension()
    {
        if (string.IsNullOrWhiteSpace(ExportPathBox.Text))
        {
            return;
        }

        var format = (ExportFormatBox.SelectedItem as ExportFormatItem)?.Format;
        var directory = Path.GetDirectoryName(ExportPathBox.Text);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(ExportPathBox.Text);
        var newFileName = nameWithoutExtension + ExportExtension(format);

        ExportPathBox.Text = string.IsNullOrEmpty(directory)
            ? newFileName
            : Path.Combine(directory, newFileName);
    }

    private void UpdateModeState()
    {
        var toDisk = CloneToDiskOption.IsChecked == true;
        TargetDiskBox.IsEnabled = toDisk && _targets.Count > 0;
        ExportPathBox.IsEnabled = !toDisk;
        ExportFormatBox.IsEnabled = !toDisk;
        CompressionLevelBox.IsEnabled = !toDisk;
    }
}