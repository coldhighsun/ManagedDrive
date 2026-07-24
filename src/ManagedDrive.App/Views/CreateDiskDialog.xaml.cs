using System.Windows.Controls;
using System.Windows.Input;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="CreateDiskDialog"/>.
/// </summary>
public partial class CreateDiskDialog
{
    private static readonly List<ImageCompressionLevel> CompressionLevels = [
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
    private readonly string? _originalPassword;
    private readonly IReadOnlyList<DiskOptions> _otherDisks;
    private readonly bool _wasEncrypted;
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
    /// <param name="currentPassword">
    /// The disk's current password (<c>RamDisk.CurrentPassword</c>), or <c>null</c> if the disk's
    /// backing image is not currently password-protected. When set, the encryption checkbox is
    /// checked and both password fields are pre-filled with the current password so it can be
    /// seen and edited in place; leaving the fields unchanged means "keep the current password
    /// unchanged" rather than "remove it".
    /// </param>
    public CreateDiskDialog(DiskOptions existing, IReadOnlyList<DiskOptions>? otherDisks = null, string? currentPassword = null) : this(otherDisks)
    {
        Title = Loc.Get("CreateDisk.TitleEdit");
        _originalPassword = currentPassword;
        _wasEncrypted = currentPassword is not null;
        EncryptImageBox.IsChecked = _wasEncrypted;
        if (_wasEncrypted)
        {
            PasswordBox1.Password = currentPassword ?? string.Empty;
            PasswordBox2.Password = currentPassword ?? string.Empty;
        }

        DriveLetterBox.Items.Clear();
        LoadDriveLetters(reservedLetter: existing.MountPoint[0]);
        DriveLetterBox.SelectedItem = existing.MountPoint;

        VolumeLabelBox.Text = existing.VolumeLabel;

        var (capacityValue, capacityIsGb) = ByteUnitConverter.SplitToUnit(existing.CapacityBytes);
        CapacityUnitBox.SelectedItem = capacityIsGb ? "GB" : "MB";
        _capacityValue = capacityValue;

        _capacityMaximum = GetMaxCapacityValue();
        CapacitySlider.Maximum = _capacityMaximum;
        UpdateCapacityDisplay();

        ReadOnlyBox.IsChecked = existing.ReadOnly;
        AutoMountBox.IsChecked = existing.AutoMount;
        ImagePathBox.Text = existing.PersistImagePath ?? string.Empty;
        CompressionLevelBox.SelectedIndex = CompressionLevels.IndexOf(existing.CompressionLevel);
        SaveOnExitBox.IsChecked = existing.SaveImageOnExit;
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

                var (sizeValue, sizeIsGb) = ByteUnitConverter.SplitToUnit(maxSizeBytes);
                SnapshotSizeUnitBox.SelectedItem = sizeIsGb ? "GB" : "MB";
                SnapshotSizeSlider.Maximum = GetMaxSnapshotSizeValue();
                SnapshotSizeValue = sizeValue;
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

        var (importCapValue, importCapIsGb) = ByteUnitConverter.SplitToUnit(importCapacityBytes);
        CapacityUnitBox.SelectedItem = importCapIsGb ? "GB" : "MB";
        _capacityValue = importCapValue;

        _capacityMaximum = Math.Max(_capacityValue, GetMaxCapacityValue());
        CapacitySlider.Maximum = _capacityMaximum;
        UpdateCapacityDisplay();
        VolumeLabelBox.Text = importVolumeLabel;

        CapacitySlider.IsEnabled = false;
        CapacityUnitBox.IsEnabled = false;
        VolumeLabelBox.IsEnabled = false;

        // Whether the imported image is encrypted is inherent to the file being imported, not a
        // choice made here — the password needed to unlock it (if any) is prompted for after
        // mounting fails with ImagePasswordRequiredException, not via this checkbox.
        EncryptImageBox.IsChecked = false;
        EncryptImageBox.IsEnabled = false;

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
    /// <param name="isArchiveImport">Always <c>true</c>; disambiguate this overload from the image-import constructor.</param>
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

        var (archiveCapValue, archiveCapIsGb) = ByteUnitConverter.SplitToUnit(importTotalBytes);
        CapacityUnitBox.SelectedItem = archiveCapIsGb ? "GB" : "MB";
        _capacityValue = archiveCapValue;

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
    /// The plaintext password entered by the user, when <see cref="PasswordChanged"/> is
    /// <c>true</c> and this is non-null (set/change password); <c>null</c> together with
    /// <see cref="PasswordChanged"/> <c>true</c> means "remove password protection". Never part
    /// of <see cref="DiskOptions"/> — it is not persisted anywhere and must be passed directly to
    /// <c>RamDisk.Create</c>/<c>RamDisk.SetPassword</c> by the caller.
    /// </summary>
    public string? Password
    {
        get; private set;
    }

    /// <summary>
    /// <c>true</c> when the user's input requires a password change (setting, changing, or
    /// removing it); <c>false</c> when editing an already-encrypted disk and the password fields
    /// were left blank, meaning "keep the current password unchanged".
    /// </summary>
    public bool PasswordChanged
    {
        get; private set;
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

    private void AutoSaveBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        UpdateAutoSaveIntervalPanelState();
        UpdateSnapshotEnabledState();
    }

    private void AutoSaveIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        IntervalValue = (int)e.NewValue;
    }

    /// <summary>
    /// Snapshots the current control values into a WPF-free <see cref="CreateDiskInput"/>.
    /// </summary>
    private CreateDiskInput BuildInput()
    {
        var mode = _isArchiveImportMode
            ? CreateDiskMode.ImportArchive
            : _isImportMode
                ? CreateDiskMode.ImportImage
                : CreateDiskMode.Create;

        return new()
        {
            MountPoint = DriveLetterBox.SelectedItem as string,
            Mode = mode,
            ImportCapacityBytes = _importCapacityBytes,
            ImportVolumeLabel = _importVolumeLabel,
            ImportArchivePath = _importArchivePath,
            CapacityValue = _capacityValue,
            CapacityIsGb = CapacityUnitBox.SelectedItem as string == "GB",
            MaxCapacityValue = GetMaxCapacityValue(),
            VolumeLabel = VolumeLabelBox.Text,
            ImagePathText = ImagePathBox.Text,
            IsReadOnly = ReadOnlyBox.IsChecked == true,
            AutoMount = AutoMountBox.IsChecked == true,
            AutoSaveEnabled = AutoSaveBox.IsChecked == true,
            IntervalValue = _intervalValue,
            SnapshotCountEnabled = SnapshotCountEnabledBox.IsChecked == true,
            SnapshotCountValue = _snapshotCountValue,
            SnapshotSizeEnabled = SnapshotSizeEnabledBox.IsChecked == true,
            SnapshotSizeValue = _snapshotSizeValue,
            SnapshotSizeIsGb = SnapshotSizeUnitBox.SelectedItem as string == "GB",
            HighUsageWarnEnabled = HighUsageWarnBox.IsChecked == true,
            HighUsageWarnPercentValue = _highUsageWarnPercentValue,
            CompressionLevel = (CompressionLevelBox.SelectedItem as CompressionLevelItem)?.Level
                ?? ImageCompressionLevel.Fastest,
            SaveImageOnExit = SaveOnExitBox.IsChecked == true,
            EncryptChecked = EncryptImageBox.IsChecked == true,
            Password1 = PasswordBox1.Password,
            Password2 = PasswordBox2.Password,
            WasEncrypted = _wasEncrypted,
            OriginalPassword = _originalPassword,
            OtherDisks = _otherDisks,
        };
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

    private int ComputeMaxValueForUnit(bool isGb) => ByteUnitConverter.MaxValueForUnit(_maxCapacityBytes, isGb);

    private void EncryptImageBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        PasswordPanel.IsEnabled = EncryptImageBox.IsChecked == true;
        UpdatePasswordStrengthHint();
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

    /// <summary>
    /// Maps a <see cref="CreateDiskValidationError"/> to its localized message.
    /// </summary>
    private string MapError(CreateDiskValidationError error) => error switch
    {
        CreateDiskValidationError.NoDriveLetter => Loc.Get("Val.NoDriveLetter"),
        CreateDiskValidationError.BadCapacity =>
            string.Format(Loc.Get("Val.BadCapacity"), GetMaxCapacityValue(), CapacityUnitBox.SelectedItem),
        CreateDiskValidationError.BadImagePath => Loc.Get("Val.BadImagePath"),
        CreateDiskValidationError.ImagePathIsSnapshot => Loc.Get("Val.ImagePathIsSnapshot"),
        CreateDiskValidationError.ImagePathInUse => Loc.Get("Val.ImagePathInUse"),
        CreateDiskValidationError.ImagePathOnRamDisk => Loc.Get("Val.ImagePathOnRamDisk"),
        CreateDiskValidationError.ReadOnlyRequiresImage => Loc.Get("Val.ReadOnlyRequiresImage"),
        CreateDiskValidationError.ReadOnlyImageNotFound => Loc.Get("Val.ReadOnlyImageNotFound"),
        CreateDiskValidationError.AutoSaveNoImage => Loc.Get("Val.AutoSaveNoImage"),
        CreateDiskValidationError.BadAutoSaveInterval => Loc.Get("Val.BadAutoSaveInterval"),
        CreateDiskValidationError.BadSnapshotCount => Loc.Get("Val.BadSnapshotCount"),
        CreateDiskValidationError.BadSnapshotSize => Loc.Get("Val.BadSnapshotSize"),
        CreateDiskValidationError.BadHighUsagePercent => Loc.Get("Val.BadHighUsagePercent"),
        CreateDiskValidationError.PasswordRequired => Loc.Get("Val.PasswordRequired"),
        CreateDiskValidationError.PasswordMismatch => Loc.Get("Val.PasswordMismatch"),
        CreateDiskValidationError.PasswordTooShort =>
            Loc.Format("Val.PasswordTooShort", CreateDiskOptionsBuilder.MinPasswordLength),
        CreateDiskValidationError.PasswordTooLong =>
            Loc.Format("Val.PasswordTooLong", CreateDiskOptionsBuilder.MaxPasswordLength),
        _ => Loc.Get("Val.Title"),
    };

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
            var availability = CreateDiskOptionsBuilder.ValidateImagePathAvailable(dlg.FileName, _otherDisks);
            if (availability != CreateDiskValidationError.None)
            {
                MessageBox.Show(MapError(availability), Loc.Get("Val.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ImagePathBox.Text = dlg.FileName;
            UpdateCompressionLevelState();
            UpdateAutoSaveEnabledState();
        }
    }

    private void PasswordBox1_PasswordChanged(object sender, RoutedEventArgs e)
    {
        UpdatePasswordStrengthHint();
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

    /// <summary>
    /// Reads the dialog's controls into a <see cref="CreateDiskInput"/> and validates/builds the
    /// options via <see cref="CreateDiskOptionsBuilder"/>. On success, <see cref="Result"/>,
    /// <see cref="Password"/>, and <see cref="PasswordChanged"/> are populated by the caller path.
    /// </summary>
    private bool TryBuildOptions(out DiskOptions? options, out string error)
    {
        options = null;
        Password = null;
        PasswordChanged = false;

        var input = BuildInput();
        var result = CreateDiskOptionsBuilder.Build(input);

        if (!result.Success)
        {
            error = MapError(result.Error);
            return false;
        }

        options = result.Options;
        Password = result.Password;
        PasswordChanged = result.PasswordChanged;
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

        // Save-on-exit only matters for a writable, persisted disk. Leave its checked state
        // untouched when disabled so the default (on) survives toggling read-only / image path.
        SaveOnExitBox.IsEnabled = hasImagePath && ReadOnlyBox.IsChecked != true;

        EncryptImageBox.IsEnabled = hasImagePath && ReadOnlyBox.IsChecked != true && !_isImportMode;
        if (!EncryptImageBox.IsEnabled)
        {
            EncryptImageBox.IsChecked = false;
        }

        PasswordPanel.IsEnabled = EncryptImageBox.IsChecked == true;

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
        // Toggle the whole row (label + combo) so the label greys out with the control when a
        // read-only disk has nothing to compress.
        CompressionLevelRow.IsEnabled = !string.IsNullOrEmpty(ImagePathBox.Text) && ReadOnlyBox.IsChecked != true;
        UpdateCompressionWarning();
    }

    private void UpdateCompressionWarning()
    {
        if (CompressionWarningText is null)
            return;
        var level = (CompressionLevelBox.SelectedItem as CompressionLevelItem)?.Level;
        var show = CompressionLevelRow.IsEnabled
                   && level is ImageCompressionLevel.Optimal or ImageCompressionLevel.SmallestSize;
        CompressionWarningText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateHighUsageWarnPercentState()
    {
        HighUsageWarnPercentPanel?.IsEnabled = HighUsageWarnBox.IsChecked == true;
    }

    private void UpdatePasswordStrengthHint()
    {
        if (EncryptImageBox.IsChecked != true || PasswordBox1.Password.Length == 0)
        {
            PasswordStrengthText.Visibility = Visibility.Collapsed;
            return;
        }

        var (key, brushKey) = PasswordStrengthEstimator.Estimate(PasswordBox1.Password) switch
        {
            PasswordStrength.Weak => ("CreateDisk.PasswordStrengthWeak", "AppWarning"),
            PasswordStrength.Medium => ("CreateDisk.PasswordStrengthMedium", "AppForegroundLight"),
            _ => ("CreateDisk.PasswordStrengthStrong", "AppForegroundLight"),
        };

        PasswordStrengthText.Text = Loc.Get(key);
        PasswordStrengthText.Foreground = (System.Windows.Media.Brush)FindResource(brushKey);
        PasswordStrengthText.Visibility = Visibility.Visible;
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

        SnapshotLimit_CheckedChanged(this, new());
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