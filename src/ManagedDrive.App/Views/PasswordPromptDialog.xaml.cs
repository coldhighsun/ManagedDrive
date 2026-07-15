using ManagedDrive.Cli.Core;
using System.Windows.Input;

namespace ManagedDrive.App.Views;

/// <summary>
/// Prompts the user for a password to unlock an encrypted disk image. Used for auto-mount at
/// startup, manual mount/import when <c>ImagePasswordRequiredException</c>/
/// <c>ImagePasswordIncorrectException</c> is thrown, and Import Disk when the selected image is
/// detected as encrypted.
/// </summary>
public partial class PasswordPromptDialog
{
    /// <summary>
    /// Initializes the dialog with the given title and, optionally, an error message shown above
    /// the password field (used when re-prompting after an incorrect password).
    /// </summary>
    /// <param name="title">Header text shown next to the icon.</param>
    /// <param name="errorMessage">
    /// Error message to show (e.g. "incorrect password"), or <see langword="null"/> for a plain prompt.
    /// </param>
    /// <param name="diskOptions">
    /// The disk being unlocked, used to show its label, mount point, and capacity in a separate
    /// info card above the password field, or <see langword="null"/> to omit that card.
    /// </param>
    public PasswordPromptDialog(string title, string? errorMessage = null, DiskOptions? diskOptions = null)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;

        if (errorMessage is not null)
        {
            ErrorText.Text = errorMessage;
            ErrorText.Visibility = Visibility.Visible;
        }

        if (diskOptions is not null)
        {
            DiskLabelText.Text = diskOptions.VolumeLabel;
            DiskDriveText.Text = diskOptions.MountPoint;
            DiskSizeText.Text = ByteFormatter.Format(diskOptions.CapacityBytes);
            DiskInfoPanel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Gets the password entered by the user after <c>OK</c> is pressed.
    /// </summary>
    public string Password => PasswordInput.Password;

    private void OkButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}