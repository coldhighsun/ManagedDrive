using ManagedDrive.Core;
using System.IO;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="CreateDiskDialog"/>.
/// </summary>
public partial class CreateDiskDialog
{
    /// <summary>
    /// Initializes the dialog.
    /// </summary>
    public CreateDiskDialog()
    {
        InitializeComponent();
        LoadDriveLetters();
    }

    /// <summary>
    /// Gets the <see cref="DiskOptions"/> built from user input after <c>OK</c> is pressed.
    /// <c>null</c> when the dialog was cancelled.
    /// </summary>
    public DiskOptions? Result
    {
        get; private set;
    }

    private void BrowseImagePath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Select Image File",
            Filter = "ManagedDrive Image (*.mdr)|*.mdr|All Files (*.*)|*.*",
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
            MessageBox.Show(error, "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            error = "Please select a drive letter.";
            return false;
        }

        if (!uint.TryParse(CapacityBox.Text.Trim(), out var capacityMb) || capacityMb == 0)
        {
            error = "Capacity must be a positive integer (in MB).";
            return false;
        }

        var imagePath = string.IsNullOrWhiteSpace(ImagePathBox.Text)
            ? null
            : ImagePathBox.Text.Trim();

        options = new DiskOptions
        {
            MountPoint = mountPoint,
            VolumeLabel = VolumeLabelBox.Text.Trim(),
            CapacityBytes = (ulong)capacityMb * 1024 * 1024,
            ReadOnly = ReadOnlyBox.IsChecked == true,
            AutoMount = AutoMountBox.IsChecked == true,
            PersistImagePath = imagePath,
        };

        error = string.Empty;
        return true;
    }
}