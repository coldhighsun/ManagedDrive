using ManagedDrive.App.Localization;
using ManagedDrive.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    /// Initializes the dialog.
    /// </summary>
    public CreateDiskDialog()
    {
        InitializeComponent();
        LoadDriveLetters();
        VolumeLabelBox.Text = Loc.Get("CreateDisk.DefaultLabel");
        _maxCapacityBytes = (ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        CapacityUnitBox.Items.Add("MB");
        CapacityUnitBox.Items.Add("GB");
        CapacityUnitBox.SelectedIndex = 1; // default: GB — attach SelectionChanged after to avoid firing during init
        CapacityUnitBox.SelectionChanged += CapacityUnitBox_SelectionChanged;
        DataObject.AddPastingHandler(CapacityBox, CapacityBox_Pasting);
    }

    /// <summary>
    /// Gets the <see cref="DiskOptions"/> built from user input after <c>OK</c> is pressed.
    /// <c>null</c> when the dialog was cancelled.
    /// </summary>
    public DiskOptions? Result
    {
        get; private set;
    }

    private uint GetMaxCapacityValue()
    {
        var isGb = CapacityUnitBox.SelectedItem as string == "GB";
        var divisor = isGb ? 1024UL * 1024 * 1024 : 1024UL * 1024;
        return (uint)Math.Max(1, Math.Min(_maxCapacityBytes / divisor, uint.MaxValue));
    }

    private void CapacityBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void CapacityBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            var text = (string)e.DataObject.GetData(typeof(string))!;
            if (!text.All(char.IsDigit))
                e.CancelCommand();
        }
        else
        {
            e.CancelCommand();
        }
    }

    private void CapacityUnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var max = GetMaxCapacityValue();
        if (uint.TryParse(CapacityBox.Text.Trim(), out var current) && current > max)
            CapacityBox.Text = max.ToString();
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

    private void LoadDriveLetters()
    {
        var usedLetters = new HashSet<char>(
            DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));

        for (var c = 'C'; c <= 'Z'; c++)
        {
            if (!usedLetters.Contains(c))
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
        if (!uint.TryParse(CapacityBox.Text.Trim(), out var capacityValue)
            || capacityValue == 0
            || capacityValue > maxCapacity)
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