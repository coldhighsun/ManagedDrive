namespace ManagedDrive.App.Views;

/// <summary>
/// The action the user chose in <see cref="UpdateAvailableDialog"/>.
/// </summary>
public enum UpdateDialogAction
{
    RemindLater,
    Skip,
    ViewRelease,
}

/// <summary>
/// Interaction logic for <see cref="UpdateAvailableDialog"/>.
/// </summary>
public partial class UpdateAvailableDialog
{
    private readonly UpdateInfo _info;

    /// <summary>
    /// Shows a newer-version-available prompt for <paramref name="info"/>.
    /// </summary>
    public UpdateAvailableDialog(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        TitleText.Text = Loc.Get("Update.DialogTitle");
        BodyText.Text = Loc.Format("Update.DialogBody", info.Version);
    }

    /// <summary>
    /// The action the user chose. Defaults to <see cref="UpdateDialogAction.RemindLater"/> if the
    /// dialog is dismissed without an explicit button click.
    /// </summary>
    public UpdateDialogAction Action { get; private set; } = UpdateDialogAction.RemindLater;

    private void RemindLaterButton_Click(object sender, RoutedEventArgs e)
    {
        Action = UpdateDialogAction.RemindLater;
        DialogResult = true;
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        Action = UpdateDialogAction.Skip;
        DialogResult = true;
    }

    private void ViewReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        Action = UpdateDialogAction.ViewRelease;
        Process.Start(new ProcessStartInfo(_info.ReleaseUrl.AbsoluteUri) { UseShellExecute = true });
        DialogResult = true;
    }
}