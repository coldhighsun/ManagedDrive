using ManagedDrive.App.Localization;
using ManagedDrive.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="CreateDiskDialog"/>.
/// </summary>
public partial class CreateDiskDialog
{
    private readonly ulong _maxCapacityBytes;

    /// <summary>
    /// Initializes the dialog in create mode.
    /// </summary>
    public CreateDiskDialog()
    {
        InitializeComponent();
        _maxCapacityBytes = (ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        LoadDriveLetters(reservedLetter: null);
        VolumeLabelBox.Text = Loc.Get("CreateDisk.DefaultLabel");
        CapacityUnitBox.Items.Add("MB");
        CapacityUnitBox.Items.Add("GB");
        CapacityUnitBox.SelectedIndex = 1; // default: GB — attach SelectionChanged after to avoid firing during init
        CapacityUnitBox.SelectionChanged += CapacityUnitBox_SelectionChanged;
        CapacityBox.Maximum = GetMaxCapacityValue();
    }

    /// <summary>
    /// Initializes the dialog in edit mode, pre-populating all fields from <paramref name="existing"/>.
    /// </summary>
    /// <param name="existing">The options of the disk being edited.</param>
    public CreateDiskDialog(DiskOptions existing) : this()
    {
        Title = Loc.Get("CreateDisk.TitleEdit");

        // Re-populate drive letters including the one currently in use.
        DriveLetterBox.Items.Clear();
        LoadDriveLetters(reservedLetter: existing.MountPoint[0]);
        DriveLetterBox.SelectedItem = existing.MountPoint;

        VolumeLabelBox.Text = existing.VolumeLabel;

        var capacityMb = existing.CapacityBytes / (1024UL * 1024);
        var capacityGb = existing.CapacityBytes / (1024UL * 1024 * 1024);
        if (capacityGb > 0 && existing.CapacityBytes % (1024UL * 1024 * 1024) == 0)
        {
            CapacityUnitBox.SelectedItem = "GB";
            CapacityBox.Value = (int)capacityGb;
        }
        else
        {
            CapacityUnitBox.SelectedItem = "MB";
            CapacityBox.Value = (int)capacityMb;
        }

        ReadOnlyBox.IsChecked = existing.ReadOnly;
        AutoMountBox.IsChecked = existing.AutoMount;
        ImagePathBox.Text = existing.PersistImagePath ?? string.Empty;
    }

    /// <summary>
    /// Gets the <see cref="DiskOptions"/> built from user input after <c>OK</c> is pressed.
    /// <c>null</c> when the dialog was cancelled.
    /// </summary>
    public DiskOptions? Result
    {
        get; private set;
    }

    private int GetMaxCapacityValue()
    {
        var isGb = CapacityUnitBox.SelectedItem as string == "GB";
        var divisor = isGb ? 1024UL * 1024 * 1024 : 1024UL * 1024;
        return (int)Math.Max(1, Math.Min(_maxCapacityBytes / divisor, int.MaxValue));
    }

    private void CapacityUnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var max = GetMaxCapacityValue();
        CapacityBox.Maximum = max;
        if (CapacityBox.Value > max)
        {
            CapacityBox.Value = max;
        }
    }

    private void ClearImagePath_Click(object sender, RoutedEventArgs e)
    {
        ImagePathBox.Text = string.Empty;
    }

    private void BrowseImagePath_Click(object sender, RoutedEventArgs e)
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
        }
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

    private bool TryBuildOptions(out DiskOptions? options, out string error)
    {
        options = null;

        if (DriveLetterBox.SelectedItem is not string mountPoint)
        {
            error = Loc.Get("Val.NoDriveLetter");
            return false;
        }

        var maxCapacity = GetMaxCapacityValue();
        var capacityValue = CapacityBox.Value;
        if (capacityValue <= 0 || capacityValue > maxCapacity)
        {
            error = string.Format(Loc.Get("Val.BadCapacity"), maxCapacity, CapacityUnitBox.SelectedItem);
            return false;
        }

        var isGb = CapacityUnitBox.SelectedItem as string == "GB";
        var capacityBytes = isGb
            ? (ulong)capacityValue * 1024 * 1024 * 1024
            : (ulong)capacityValue * 1024 * 1024;

        var imagePath = string.IsNullOrWhiteSpace(ImagePathBox.Text)
            ? null
            : ImagePathBox.Text.Trim();

        options = new DiskOptions
        {
            MountPoint = mountPoint,
            VolumeLabel = VolumeLabelBox.Text.Trim(),
            CapacityBytes = capacityBytes,
            ReadOnly = ReadOnlyBox.IsChecked == true,
            AutoMount = AutoMountBox.IsChecked == true,
            PersistImagePath = imagePath,
        };

        error = string.Empty;
        return true;
    }
}
