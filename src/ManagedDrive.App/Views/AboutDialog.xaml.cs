using System.Windows.Navigation;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="AboutDialog"/>.
/// </summary>
public partial class AboutDialog
{
    private const string GitHubUrl = "https://github.com/coldhighsun/ManagedDrive";
    private const string ThirdPartyNoticesUrl = "https://github.com/coldhighsun/ManagedDrive/blob/main/docs/THIRD-PARTY-NOTICES.md";
    private readonly UpdateCheckService? _updateCheckService;
    private UpdateInfo? _updateInfo;

    /// <summary>
    /// Initializes a new instance of the <see cref="AboutDialog"/> class and starts an update check when a service is supplied.
    /// </summary>
    public AboutDialog(UpdateCheckService? updateCheckService = null)
    {
        InitializeComponent();
        _updateCheckService = updateCheckService;

        VersionText.Text = UpdateCheckService.GetRunningVersion();
        GitHubLink.NavigateUri = new(GitHubUrl);
        ThirdPartyNoticesLink.NavigateUri = new(ThirdPartyNoticesUrl);

        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        if (_updateCheckService == null)
        {
            return;
        }

        var (success, info) = await _updateCheckService.CheckSilentlyAsync();
        if (!success || info == null)
        {
            return;
        }

        _updateInfo = info;
        UpdateLinkRun.Text = Loc.Format("About.UpdateAvailable", info.Version);
        UpdateStatusText.Visibility = Visibility.Visible;
    }

    private void UpdateLink_Click(object sender, RoutedEventArgs e)
    {
        if (_updateCheckService == null || _updateInfo == null)
        {
            return;
        }

        if (UpdateDialog.ShowFor(_updateCheckService, _updateInfo, this))
        {
            // The installer has been started and will ask the app to exit; nothing else should stay open.
            Close();
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
