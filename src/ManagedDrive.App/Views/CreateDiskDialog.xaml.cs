using System.Windows.Controls;
using System.Windows.Input;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="CreateDiskDialog"/>.
/// </summary>
public partial class CreateDiskDialog
{
    private static readonly List<ImageCompressionLevel> CompressionLevels =
    [
        ImageCompressionLevel.None,
        ImageCompressionLevel.Fastest,
        ImageCompressionLevel.Optimal,
        ImageCompressionLevel.SmallestSize,
    ];

    private readonly string _importArchivePath = string.Empty;
    private readonly ulong _importCapacityBytes;
    private readonly string _importVolumeLabel = string.Empty;
    private readonly bool _isArchiveImportMode;
    private readonly bool _isImportMode;
    private readonly ulong _maxCapacityBytes;
    private readonly IReadOnlyList<DiskOptions> _otherDisks;
    private int _capacityMaximum = 99999999;
    private int _capacityValue = 2;
    private int _highUsageWarnPercentValue = 90;
    private int _intervalValue = 10;
    private int _snapshotCountValue = 10;
    private int _snapshotSizeValue = 2;

    /// <summary>
    /// Initializes the dialog in create mode.
    /// </summary>
    /// <param name="otherDisks">
    /// Options of all other currently active disks, used to validate that the image file path
    /// does not collide with another disk's mount point or image file.
    /// </param>
    public CreateDiskDialog(IReadOnlyList<DiskOptions>? otherDisks = null)
    {
        InitializeComponent();
        _otherDisks = otherDisks ?? [];
        _maxCapacityBytes = (ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        LoadDriveLetters(reservedLetter: null);
        VolumeLabelBox.Text = Loc.Get("CreateDisk.DefaultLabel");
        CapacityUnitBox.Items.Add("MB");
        CapacityUnitBox.Items.Add("GB");
        CapacityUnitBox.SelectedIndex = 1;
        CapacityUnitBox.SelectionChanged += CapacityUnitBox_SelectionChanged;
        _capacityMaximum = GetMaxCapacityValue();
        CapacitySlider.Maximum = _capacityMaximum;
        UpdateCapacityDisplay();

        SnapshotSizeUnitBox.Items.Add("MB");
        SnapshotSizeUnitBox.Items.Add("GB");
        SnapshotSizeUnitBox.SelectedIndex = 1;
        SnapshotSizeUnitBox.SelectionChanged += SnapshotSizeUnitBox_SelectionChanged;
        SnapshotSizeSlider.Maximum = GetMaxSnapshotSizeValue();
        UpdateSnapshotCountDisplay();
        UpdateSnapshotSizeDisplay();
        IntervalValue = _intervalValue;
        HighUsageWarnPercentValue = _highUsageWarnPercentValue;

        foreach (var level in CompressionLevels)
        {
            CompressionLevelBox.Items.Add(new CompressionLevelItem(level, Loc.Get(CompressionLevelKey(level))));
        }

        CompressionLevelBox.SelectedIndex = CompressionLevels.IndexOf(ImageCompressionLevel.Fastest);
        UpdateCompressionLevelState();
        UpdateAutoSaveEnabledState();
        UpdateHighUsageWarnPercentState();
    }

    /// <summary>
    /// Initializes the dialog in edit mode, pre-populating all fields from <paramref name="existing"/>.
    /// </summary>
    /// <param name="existing">The options of the disk being edited.</param>
    /// <param name="otherDisks">
    /// Options of all other currently active disks (excluding <paramref name="existing"/>), used
    /// to validate that the image file path does not collide with another disk's mount point or
    /// image file.
    /// </param>
    public CreateDiskDialog(DiskOptions existing, IReadOnlyList<DiskOptions>? otherDisks = null) : this(otherDisks)
    {
        Title = Loc.Get("CreateDisk.TitleEdit");

        DriveLetterBox.Items.Clear();
        LoadDriveLetters(reservedLetter: existing.MountPoint[0]);
        DriveLetterBox.SelectedItem = existing.MountPoint;

        VolumeLabelBox.Text = existing.VolumeLabel;

        var capacityMb = existing.CapacityBytes / (1024UL * 1024);
        var capacityGb = existing.CapacityBytes / (1024UL * 1024 * 1024);
        if (capacityGb > 0 && existing.CapacityBytes % (1024UL * 1024 * 1024) == 0)
        {
            CapacityUnitBox.SelectedItem = "GB";
            _capacityValue = (int)capacityGb;
        }
        else
        {
            CapacityUnitBox.SelectedItem = "MB";
            _capacityValue = (int)capacityMb;
        }

        _capacityMaximum = GetMaxCapacityValue();
        CapacitySlider.Maximum = _capacityMaximum;
        UpdateCapacityDisplay();

        ReadOnlyBox.IsChecked = existing.ReadOnly;
        AutoMountBox.IsChecked = existing.AutoMount;
        ImagePathBox.Text = existing.PersistImagePath ?? string.Empty;
        CompressionLevelBox.SelectedIndex = CompressionLevels.IndexOf(existing.CompressionLevel);
        UpdateCompressionLevelState();
        UpdateAutoSaveEnabledState();

        if (existing is { AutoSaveIntervalMinutes: { } minutes, ReadOnly: false })
        {
            AutoSaveBox.IsChecked = true;
            AutoSaveIntervalPanel.IsEnabled = true;
            IntervalValue = (int)Math.Max(1, minutes);

            if (existing.MaxSnapshotCount is { } maxCount)
            {
                SnapshotCountEnabledBox.IsChecked = true;
                SnapshotCountPanel.IsEnabled = true;
                SnapshotCountValue = (int)Math.Max(1, maxCount);
            }

            if (existing.MaxSnapshotSizeBytes is { } maxSizeBytes)
            {
                SnapshotSizeEnabledBox.IsChecked = true;
                SnapshotSizePanel.IsEnabled = true;

                var sizeMb = maxSizeBytes / (1024UL * 1024);
                var sizeGb = maxSizeBytes / (1024UL * 1024 * 1024);
                if (sizeGb > 0 && maxSizeBytes % (1024UL * 1024 * 1024) == 0)
                {
                    SnapshotSizeUnitBox.SelectedItem = "GB";
                    SnapshotSizeSlider.Maximum = GetMaxSnapshotSizeValue();
                    SnapshotSizeValue = (int)sizeGb;
                }
                else
                {
                    SnapshotSizeUnitBox.SelectedItem = "MB";
                    SnapshotSizeSlider.Maximum = GetMaxSnapshotSizeValue();
                    SnapshotSizeValue = (int)Math.Max(1, sizeMb);
                }
            }
        }

        if (existing.HighUsageWarnPercent is { } warnPercent)
        {
            HighUsageWarnBox.IsChecked = true;
            HighUsageWarnPercentValue = (int)Math.Clamp(warnPercent, 1, 99);
        }
        else
        {
            HighUsageWarnBox.IsChecked = false;
        }

        UpdateHighUsageWarnPercentState();

        if (existing.SourceArchivePath is { } sourceArchivePath)
        {
            // Archive-sourced disks keep the same restrictions as the "Import Archive" flow:
            // capacity/label/read-only are locked to the archive's own values, there's no
            // backing image file to configure persistence for, and the high-usage warning is
            // unavailable (forced off) rather than just defaulted.
            _isImportMode = true;
            _isArchiveImportMode = true;
            _importArchivePath = sourceArchivePath;
            _importCapacityBytes = existing.CapacityBytes;
            _importVolumeLabel = existing.VolumeLabel;

            ImagePathLabel.Text = Loc.Get("CreateDisk.ArchiveFile");
            ImagePathBox.Text = sourceArchivePath;
            ClearImagePathButton.IsEnabled = false;
            ArchiveImportNoteText.Visibility = Visibility.Visible;

            CapacityRow.IsEnabled = false;
            VolumeLabelBox.IsEnabled = false;

            ReadOnlyBox.IsChecked = true;
            ReadOnlyBox.IsEnabled = false;
            PersistenceTabItem.IsEnabled = false;

            HighUsageWarnBox.IsChecked = false;
            HighUsageWarnBox.IsEnabled = false;
            UpdateHighUsageWarnPercentState();
        }
    }

    /// <summary>
    /// Initializes the dialog in import mode: capacity and volume label are pre-filled from an
    /// existing image file and locked, since they are read from the image at mount time.
    /// </summary>
    /// <param name="importImagePath">Path of the existing image file to import.</param>
    /// <param name="importCapacityBytes">Capacity stored in the image, used to pre-fill and lock the capacity fields.</param>
    /// <param name="importVolumeLabel">Volume label stored in the image, used to pre-fill and lock the label field.</param>
    /// <param name="otherDisks">
    /// Options of all other currently active disks, used to validate that the image file path
    /// does not collide with another disk's mount point or image file.
    /// </param>
    public CreateDiskDialog(string importImagePath, ulong importCapacityBytes, string importVolumeLabel,
        IReadOnlyList<DiskOptions>? otherDisks = null) : this(otherDisks)
    {
        _isImportMode = true;
        _importCapacityBytes = importCapacityBytes;
        _importVolumeLabel = importVolumeLabel;

        Title = Loc.Get("CreateDisk.TitleImport");

        ImagePathBox.Text = importImagePath;
        ClearImagePathButton.IsEnabled = false;
        ImportNoteText.Visibility = Visibility.Visible;

        var capacityMb = importCapacityBytes / (1024UL * 1024);
        var capacityGb = importCapacityBytes / (1024UL * 1024 * 1024);
        if (capacityGb > 0 && importCapacityBytes % (1024UL * 1024 * 1024) == 0)
        {
            CapacityUnitBox.SelectedItem = "GB";
            _capacityValue = (int)capacityGb;
        }
        else
        {
            CapacityUnitBox.SelectedItem = "MB";
            _capacityValue = (int)capacityMb;
        }

        _capacityMaximum = Math.Max(_capacityValue, GetMaxCapacityValue());
        CapacitySlider.Maximum = _capacityMaximum;
        UpdateCapacityDisplay();
        VolumeLabelBox.Text = importVolumeLabel;

        CapacitySlider.IsEnabled = false;
        CapacityUnitBox.IsEnabled = false;
        VolumeLabelBox.IsEnabled = false;

        UpdateCompressionLevelState();
        UpdateAutoSaveEnabledState();
    }

    /// <summary>
    /// Initializes the dialog in archive-import mode: capacity and volume label are pre-filled
    /// from the archive's contents and locked, the disk is forced read-only (archive formats
    /// don't support writing changes back), and the entire Persistence tab is disabled since an
    /// archive-sourced disk has no backing image file to save to.
    /// </summary>
    /// <param name="importArchivePath">Path of the archive file to import.</param>
    /// <param name="importTotalBytes">Total uncompressed size of the archive's contents, used to pre-fill and lock the capacity fields.</param>
    /// <param name="importVolumeLabel">Suggested volume label (derived from the archive's file name), used to pre-fill and lock the label field.</param>
    /// <param name="otherDisks">
    /// Options of all other currently active disks, used to validate that the archive file path
    /// does not collide with another disk's mount point.
    /// </param>
    /// <param name="isArchiveImport">Always <c>true</c>; disambiguates this overload from the image-import constructor.</param>
    public CreateDiskDialog(string importArchivePath, ulong importTotalBytes, string importVolumeLabel,
        IReadOnlyList<DiskOptions> otherDisks, bool isArchiveImport) : this(otherDisks)
    {
        _ = isArchiveImport;
        _isImportMode = true;
        _isArchiveImportMode = true;
        _importArchivePath = importArchivePath;
        _importCapacityBytes = importTotalBytes;
        _importVolumeLabel = importVolumeLabel;

        Title = Loc.Get("CreateDisk.TitleImportArchive");

        ImagePathLabel.Text = Loc.Get("CreateDisk.ArchiveFile");
        ImagePathBox.Text = importArchivePath;
        ClearImagePathButton.IsEnabled = false;
        ArchiveImportNoteText.Visibility = Visibility.Visible;

        var capacityMb = importTotalBytes / (1024UL * 1024);
        var capacityGb = importTotalBytes / (1024UL * 1024 * 1024);
        if (capacityGb > 0 && importTotalBytes % (1024UL * 1024 * 1024) == 0)
        {
            CapacityUnitBox.SelectedItem = "GB";
            _capacityValue = (int)capacityGb;
        }
        else
        {
            CapacityUnitBox.SelectedItem = "MB";
            _capacityValue = (int)capacityMb;
        }

        _capacityMaximum = Math.Max(_capacityValue, GetMaxCapacityValue());
        CapacitySlider.Maximum = _capacityMaximum;
        UpdateCapacityDisplay();
        VolumeLabelBox.Text = importVolumeLabel;

        CapacityRow.IsEnabled = false;
        VolumeLabelBox.IsEnabled = false;

        ReadOnlyBox.IsChecked = true;
        ReadOnlyBox.IsEnabled = false;
        PersistenceTabItem.IsEnabled = false;

        HighUsageWarnBox.IsChecked = false;
        HighUsageWarnBox.IsEnabled = false;
        UpdateHighUsageWarnPercentState();

        UpdateCompressionLevelState();
        UpdateAutoSaveEnabledState();
    }

    /// <summary>
    /// Gets the <see cref="DiskOptions"/> built from user input after <c>OK</c> is pressed.
    /// <c>null</c> when the dialog was cancelled.
    /// </summary>
    public DiskOptions? Result
    {
        get; private set;
    }

    private int CapacityValue
    {
        get => _capacityValue;
        set
        {
            _capacityValue = Math.Clamp(value, 1, _capacityMaximum);
            UpdateCapacityDisplay();
        }
    }

    private int HighUsageWarnPercentValue
    {
        get => _highUsageWarnPercentValue;
        set
        {
            _highUsageWarnPercentValue = Math.Clamp(value, 1, 99);
            HighUsageWarnPercentSlider.Value = _highUsageWarnPercentValue;
            HighUsageWarnPercentValueText?.Text = $"{_highUsageWarnPercentValue}%";
        }
    }

    private int IntervalValue
    {
        get => _intervalValue;
        set
        {
            _intervalValue = Math.Clamp(value, 1, 60);
            AutoSaveIntervalSlider.Value = _intervalValue;
            AutoSaveIntervalValueText?.Text = Loc.Format("CreateDisk.MinutesValue", _intervalValue);
        }
    }

    private int SnapshotCountValue
    {
        get => _snapshotCountValue;
        set
        {
            _snapshotCountValue = Math.Clamp(value, 1, 20);
            UpdateSnapshotCountDisplay();
        }
    }

    private int SnapshotSizeValue
    {
        get => _snapshotSizeValue;
        set
        {
            _snapshotSizeValue = Math.Clamp(value, 1, (int)SnapshotSizeSlider.Maximum);
            UpdateSnapshotSizeDisplay();
        }
    }

    private static string CompressionLevelKey(ImageCompressionLevel level) => level switch
    {
        ImageCompressionLevel.None => "CompressionLevel.None",
        ImageCompressionLevel.Fastest => "CompressionLevel.Fastest",
        ImageCompressionLevel.SmallestSize => "CompressionLevel.SmallestSize",
        _ => "CompressionLevel.Optimal",
    };

    private sealed record CompressionLevelItem(ImageCompressionLevel Level, string Display)
    {
        public override string ToString() => Display;
    }

    private static bool IsValidImagePath(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            _ = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void AutoSaveBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        UpdateAutoSaveIntervalPanelState();
        UpdateSnapshotEnabledState();
    }

    private void AutoSaveIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        IntervalValue = (int)e.NewValue;
    }

    private void CapacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        CapacityValue = (int)e.NewValue;
    }

    private void CapacityUnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _capacityMaximum = GetMaxCapacityValue();
        CapacitySlider.Maximum = _capacityMaximum;
        if (_capacityValue > _capacityMaximum)
        {
            CapacityValue = _capacityMaximum;
        }
    }

    private void ClearImagePath_Click(object sender, RoutedEventArgs e)
    {
        if (_isImportMode)
        {
            return;
        }

        ImagePathBox.Text = string.Empty;
        UpdateCompressionLevelState();
        UpdateAutoSaveEnabledState();
    }

    private void CompressionLevelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCompressionWarning();
    }

    private int ComputeMaxValueForUnit(bool isGb)
    {
        var divisor = isGb ? 1024UL * 1024 * 1024 : 1024UL * 1024;
        return (int)Math.Max(1, Math.Min(_maxCapacityBytes / divisor, int.MaxValue));
    }

    private int GetMaxCapacityValue()
    {
        var isGb = CapacityUnitBox.SelectedItem as string == "GB";
        return ComputeMaxValueForUnit(isGb);
    }

    private int GetMaxSnapshotSizeValue()
    {
        var isGb = SnapshotSizeUnitBox.SelectedItem as string == "GB";
        return ComputeMaxValueForUnit(isGb);
    }

    private void HighUsageWarnBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        UpdateHighUsageWarnPercentState();
    }

    private void HighUsageWarnPercentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        HighUsageWarnPercentValue = (int)e.NewValue;
    }

    private void ImagePathBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isImportMode)
        {
            return;
        }

        OpenImagePathDialog();
    }

    private void LoadDriveLetters(char? reservedLetter)
    {
        var usedLetters = new HashSet<char>(
            DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));

        for (var c = 'C'; c <= 'Z'; c++)
        {
            if (!usedLetters.Contains(c) || c == reservedLetter)
            {
                DriveLetterBox.Items.Add(c + ":");
            }
        }

        if (DriveLetterBox.Items.Count > 0)
        {
            DriveLetterBox.SelectedIndex = DriveLetterBox.Items.Count - 1;
        }
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildOptions(out var options, out var error))
        {
            MessageBox.Show(error, Loc.Get("Val.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = options;
        DialogResult = true;
    }

    private void OpenImagePathDialog()
    {
        var dlg = new SaveFileDialog
        {
            Title = Loc.Get("SaveDlg.Title"),
            Filter = Loc.Get("SaveDlg.Filter"),
            DefaultExt = ".mdr",
            OverwritePrompt = false,
            FileName = ImagePathBox.Text,
        };

        if (dlg.ShowDialog() == true)
        {
            ImagePathBox.Text = dlg.FileName;
            UpdateCompressionLevelState();
            UpdateAutoSaveEnabledState();
        }
    }

    private void ReadOnlyBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        UpdateAutoSaveEnabledState();
        UpdateCompressionLevelState();
    }

    private void SnapshotCountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SnapshotCountValue = (int)e.NewValue;
    }

    private void SnapshotLimit_CheckedChanged(object sender, RoutedEventArgs e)
    {
        SnapshotCountPanel.IsEnabled = SnapshotCountEnabledBox.IsChecked == true;
        SnapshotSizePanel.IsEnabled = SnapshotSizeEnabledBox.IsChecked == true;
    }

    private void SnapshotSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SnapshotSizeValue = (int)e.NewValue;
    }

    private void SnapshotSizeUnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SnapshotSizeSlider.Maximum = GetMaxSnapshotSizeValue();
        if (_snapshotSizeValue > SnapshotSizeSlider.Maximum)
        {
            SnapshotSizeValue = (int)SnapshotSizeSlider.Maximum;
        }
    }

    private bool TryBuildOptions(out DiskOptions? options, out string error)
    {
        options = null;

        if (DriveLetterBox.SelectedItem is not string mountPoint)
        {
            error = Loc.Get("Val.NoDriveLetter");
            return false;
        }

        if (_isArchiveImportMode)
        {
            return TryBuildArchiveImportOptions(mountPoint, out options, out error);
        }

        ulong capacityBytes;
        if (_isImportMode)
        {
            capacityBytes = _importCapacityBytes;
        }
        else
        {
            var maxCapacity = GetMaxCapacityValue();
            if (_capacityValue <= 0 || _capacityValue > maxCapacity)
            {
                error = string.Format(Loc.Get("Val.BadCapacity"), maxCapacity, CapacityUnitBox.SelectedItem);
                return false;
            }

            var isGb = CapacityUnitBox.SelectedItem as string == "GB";
            capacityBytes = isGb
                ? (ulong)_capacityValue * 1024 * 1024 * 1024
                : (ulong)_capacityValue * 1024 * 1024;
        }

        var imagePath = string.IsNullOrWhiteSpace(ImagePathBox.Text)
            ? null
            : ImagePathBox.Text.Trim();

        if (imagePath != null)
        {
            if (!IsValidImagePath(imagePath))
            {
                error = Loc.Get("Val.BadImagePath");
                return false;
            }

            if (SnapshotManager.IsSnapshotFileName(Path.GetFileName(imagePath)))
            {
                error = Loc.Get("Val.ImagePathIsSnapshot");
                return false;
            }

            var allMountPoints = _otherDisks.Select(d => d.MountPoint).Append(mountPoint);
            if (allMountPoints.Any(mp => imagePath.StartsWith(mp, StringComparison.OrdinalIgnoreCase)))
            {
                error = Loc.Get("Val.ImagePathOnRamDisk");
                return false;
            }

            if (_otherDisks.Any(d => d.PersistImagePath != null &&
                string.Equals(d.PersistImagePath, imagePath, StringComparison.OrdinalIgnoreCase)))
            {
                error = Loc.Get("Val.ImagePathInUse");
                return false;
            }
        }

        var isReadOnly = ReadOnlyBox.IsChecked == true;

        if (isReadOnly)
        {
            if (imagePath == null)
            {
                error = Loc.Get("Val.ReadOnlyRequiresImage");
                return false;
            }

            if (!File.Exists(imagePath))
            {
                error = Loc.Get("Val.ReadOnlyImageNotFound");
                return false;
            }
        }

        uint? autoSaveIntervalMinutes = null;
        if (AutoSaveBox.IsChecked == true && !isReadOnly)
        {
            if (imagePath == null)
            {
                error = Loc.Get("Val.AutoSaveNoImage");
                return false;
            }

            if (_intervalValue < 1 || _intervalValue > 60)
            {
                error = Loc.Get("Val.BadAutoSaveInterval");
                return false;
            }

            autoSaveIntervalMinutes = (uint)_intervalValue;
        }

        uint? maxSnapshotCount = null;
        ulong? maxSnapshotSizeBytes = null;
        if (autoSaveIntervalMinutes is not null)
        {
            if (SnapshotCountEnabledBox.IsChecked == true)
            {
                if (_snapshotCountValue < 1 || _snapshotCountValue > 20)
                {
                    error = Loc.Get("Val.BadSnapshotCount");
                    return false;
                }

                maxSnapshotCount = (uint)_snapshotCountValue;
            }

            if (SnapshotSizeEnabledBox.IsChecked == true)
            {
                if (_snapshotSizeValue < 1)
                {
                    error = Loc.Get("Val.BadSnapshotSize");
                    return false;
                }

                var isSizeGb = SnapshotSizeUnitBox.SelectedItem as string == "GB";
                maxSnapshotSizeBytes = isSizeGb
                    ? (ulong)_snapshotSizeValue * 1024 * 1024 * 1024
                    : (ulong)_snapshotSizeValue * 1024 * 1024;
            }
        }

        double? highUsageWarnPercent = null;
        if (HighUsageWarnBox.IsChecked == true)
        {
            if (_highUsageWarnPercentValue < 1 || _highUsageWarnPercentValue > 99)
            {
                error = Loc.Get("Val.BadHighUsagePercent");
                return false;
            }

            highUsageWarnPercent = _highUsageWarnPercentValue;
        }

        options = new()
        {
            MountPoint = mountPoint,
            VolumeLabel = _isImportMode ? _importVolumeLabel : VolumeLabelBox.Text.Trim(),
            CapacityBytes = capacityBytes,
            ReadOnly = isReadOnly,
            AutoMount = AutoMountBox.IsChecked == true,
            PersistImagePath = imagePath,
            AutoSaveIntervalMinutes = autoSaveIntervalMinutes,
            CompressionLevel = (CompressionLevelBox.SelectedItem as CompressionLevelItem)?.Level
                ?? ImageCompressionLevel.Fastest,
            MaxSnapshotCount = maxSnapshotCount,
            MaxSnapshotSizeBytes = maxSnapshotSizeBytes,
            HighUsageWarnPercent = highUsageWarnPercent,
        };

        error = string.Empty;
        return true;
    }

    private bool TryBuildArchiveImportOptions(string mountPoint, out DiskOptions? options, out string error)
    {
        options = null;

        double? highUsageWarnPercent = null;
        if (HighUsageWarnBox.IsChecked == true)
        {
            if (_highUsageWarnPercentValue < 1 || _highUsageWarnPercentValue > 99)
            {
                error = Loc.Get("Val.BadHighUsagePercent");
                return false;
            }

            highUsageWarnPercent = _highUsageWarnPercentValue;
        }

        options = new()
        {
            MountPoint = mountPoint,
            VolumeLabel = _importVolumeLabel,
            CapacityBytes = _importCapacityBytes,
            ReadOnly = true,
            AutoMount = AutoMountBox.IsChecked == true,
            SourceArchivePath = _importArchivePath,
            HighUsageWarnPercent = highUsageWarnPercent,
        };

        error = string.Empty;
        return true;
    }

    private void UpdateAutoSaveEnabledState()
    {
        var hasImagePath = !string.IsNullOrEmpty(ImagePathBox.Text);
        AutoSaveBox.IsEnabled = hasImagePath && ReadOnlyBox.IsChecked != true;
        if (!AutoSaveBox.IsEnabled)
        {
            AutoSaveBox.IsChecked = false;
        }

        UpdateAutoSaveIntervalPanelState();
        UpdateSnapshotEnabledState();
    }

    private void UpdateAutoSaveIntervalPanelState()
    {
        AutoSaveIntervalPanel.IsEnabled = AutoSaveBox.IsChecked == true && ReadOnlyBox.IsChecked != true;
    }

    private void UpdateCapacityDisplay()
    {
        CapacitySlider.Value = _capacityValue;
        if (CapacityValueText is null || CapacityUnitBox is null)
        {
            return;
        }

        var unit = CapacityUnitBox.SelectedItem as string ?? "GB";
        CapacityValueText.Text = $"{_capacityValue} {unit}";
    }

    private void UpdateCompressionLevelState()
    {
        CompressionLevelBox.IsEnabled = !string.IsNullOrEmpty(ImagePathBox.Text) && ReadOnlyBox.IsChecked != true;
        UpdateCompressionWarning();
    }

    private void UpdateCompressionWarning()
    {
        if (CompressionWarningText is null)
            return;
        var level = (CompressionLevelBox.SelectedItem as CompressionLevelItem)?.Level;
        var show = CompressionLevelBox.IsEnabled
                   && level is ImageCompressionLevel.Optimal or ImageCompressionLevel.SmallestSize;
        CompressionWarningText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateHighUsageWarnPercentState()
    {
        HighUsageWarnPercentPanel?.IsEnabled = HighUsageWarnBox.IsChecked == true;
    }

    private void UpdateSnapshotCountDisplay()
    {
        SnapshotCountSlider.Value = _snapshotCountValue;

        SnapshotCountValueText?.Text = _snapshotCountValue.ToString();
    }

    private void UpdateSnapshotEnabledState()
    {
        var snapshotsAllowed = AutoSaveBox.IsEnabled && AutoSaveBox.IsChecked == true;
        SnapshotCountEnabledBox.IsEnabled = snapshotsAllowed;
        SnapshotSizeEnabledBox.IsEnabled = snapshotsAllowed;
        if (!snapshotsAllowed)
        {
            SnapshotCountEnabledBox.IsChecked = false;
            SnapshotSizeEnabledBox.IsChecked = false;
        }

        SnapshotLimit_CheckedChanged(this, new RoutedEventArgs());
    }

    private void UpdateSnapshotSizeDisplay()
    {
        SnapshotSizeSlider.Value = _snapshotSizeValue;
        if (SnapshotSizeValueText is null || SnapshotSizeUnitBox is null)
        {
            return;
        }

        var unit = SnapshotSizeUnitBox.SelectedItem as string ?? "MB";
        SnapshotSizeValueText.Text = $"{_snapshotSizeValue} {unit}";
    }
}