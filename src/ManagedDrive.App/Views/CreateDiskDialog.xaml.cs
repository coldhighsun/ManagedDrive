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

    private readonly ulong _maxCapacityBytes;
    private readonly IReadOnlyList<DiskOptions> _otherDisks;
    private int _capacityMaximum;
    private int _capacityValue = 2;
    private int _intervalValue = 10;

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
        UpdateCapacityDisplay();

        foreach (var level in CompressionLevels)
        {
            CompressionLevelBox.Items.Add(new CompressionLevelItem(level, Loc.Get(CompressionLevelKey(level))));
        }

        CompressionLevelBox.SelectedIndex = CompressionLevels.IndexOf(ImageCompressionLevel.Fastest);
        UpdateCompressionLevelState();
        UpdateAutoSaveEnabledState();
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
            _intervalValue = (int)Math.Max(1, minutes);
            IntervalValue = _intervalValue;
        }
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

    private int IntervalValue
    {
        get => _intervalValue;
        set
        {
            _intervalValue = Math.Clamp(value, 1, 60);
            AutoSaveIntervalBox.Text = _intervalValue.ToString();
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
    }

    private void AutoSaveIntervalDown_Click(object sender, RoutedEventArgs e)
    {
        ParseIntervalFromBox();
        IntervalValue = _intervalValue - 1;
    }

    private void AutoSaveIntervalUp_Click(object sender, RoutedEventArgs e)
    {
        ParseIntervalFromBox();
        IntervalValue = _intervalValue + 1;
    }

    private void CapacityBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }

    private void CapacityDown_Click(object sender, RoutedEventArgs e)
    {
        ParseCapacityFromBox();
        CapacityValue = _capacityValue - 1;
    }

    private void CapacityUnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _capacityMaximum = GetMaxCapacityValue();
        if (_capacityValue > _capacityMaximum)
        {
            CapacityValue = _capacityMaximum;
        }
    }

    private void CapacityUp_Click(object sender, RoutedEventArgs e)
    {
        ParseCapacityFromBox();
        CapacityValue = _capacityValue + 1;
    }

    private void ClearImagePath_Click(object sender, RoutedEventArgs e)
    {
        ImagePathBox.Text = string.Empty;
        UpdateCompressionLevelState();
        UpdateAutoSaveEnabledState();
    }

    private int GetMaxCapacityValue()
    {
        var isGb = CapacityUnitBox.SelectedItem as string == "GB";
        var divisor = isGb ? 1024UL * 1024 * 1024 : 1024UL * 1024;
        return (int)Math.Max(1, Math.Min(_maxCapacityBytes / divisor, int.MaxValue));
    }

    private void ImagePathBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
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

    private void ParseCapacityFromBox()
    {
        if (int.TryParse(CapacityBox.Text, out var parsed))
        {
            _capacityValue = parsed;
        }
    }

    private void ParseIntervalFromBox()
    {
        if (int.TryParse(AutoSaveIntervalBox.Text, out var parsed))
        {
            _intervalValue = parsed;
        }
    }

    private void ReadOnlyBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        UpdateAutoSaveEnabledState();
        UpdateCompressionLevelState();
    }

    private bool TryBuildOptions(out DiskOptions? options, out string error)
    {
        options = null;

        if (DriveLetterBox.SelectedItem is not string mountPoint)
        {
            error = Loc.Get("Val.NoDriveLetter");
            return false;
        }

        ParseCapacityFromBox();
        var maxCapacity = GetMaxCapacityValue();
        if (_capacityValue <= 0 || _capacityValue > maxCapacity)
        {
            error = string.Format(Loc.Get("Val.BadCapacity"), maxCapacity, CapacityUnitBox.SelectedItem);
            return false;
        }

        var isGb = CapacityUnitBox.SelectedItem as string == "GB";
        var capacityBytes = isGb
            ? (ulong)_capacityValue * 1024 * 1024 * 1024
            : (ulong)_capacityValue * 1024 * 1024;

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

            ParseIntervalFromBox();
            if (_intervalValue < 1 || _intervalValue > 60)
            {
                error = Loc.Get("Val.BadAutoSaveInterval");
                return false;
            }

            autoSaveIntervalMinutes = (uint)_intervalValue;
        }

        options = new()
        {
            MountPoint = mountPoint,
            VolumeLabel = VolumeLabelBox.Text.Trim(),
            CapacityBytes = capacityBytes,
            ReadOnly = isReadOnly,
            AutoMount = AutoMountBox.IsChecked == true,
            PersistImagePath = imagePath,
            AutoSaveIntervalMinutes = autoSaveIntervalMinutes,
            CompressionLevel = (CompressionLevelBox.SelectedItem as CompressionLevelItem)?.Level
                ?? ImageCompressionLevel.Fastest,
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
    }

    private void UpdateAutoSaveIntervalPanelState()
    {
        AutoSaveIntervalPanel.IsEnabled = AutoSaveBox.IsChecked == true && ReadOnlyBox.IsChecked != true;
    }

    private void UpdateCapacityDisplay()
    {
        CapacityBox.Text = _capacityValue.ToString();
    }

    private void UpdateCompressionLevelState()
    {
        CompressionLevelBox.IsEnabled = !string.IsNullOrEmpty(ImagePathBox.Text) && ReadOnlyBox.IsChecked != true;
    }
}