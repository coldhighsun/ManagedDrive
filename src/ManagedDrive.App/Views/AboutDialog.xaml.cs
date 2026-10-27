using System.Windows.Navigation;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="AboutDialog"/>.
/// </summary>
public partial class AboutDialog
{
    private const string GitHubUrl = UpdateCheckService.RepositoryUrl;
    private const string ThirdPartyNoticesUrl = GitHubUrl + "/blob/main/docs/THIRD-PARTY-NOTICES.md";
    private readonly UpdateCheckService? _updateCheckService;

    /// <summary>
    /// Initializes a new instance of the <see cref="AboutDialog"/> class and shows the update the service's automatic check has found.
    /// </summary>
    public AboutDialog(UpdateCheckService? updateCheckService = null)
    {
        InitializeComponent();
        _updateCheckService = updateCheckService;

        VersionText.Text = UpdateCheckService.GetRunningVersion();
        GitHubLink.NavigateUri = new(GitHubUrl);
        ThirdPartyNoticesLink.NavigateUri = new(ThirdPartyNoticesUrl);

        if (updateCheckService != null)
        {
            updateCheckService.Controller.Changed += OnUpdateStateChanged;
            Closed += (_, _) => updateCheckService.Controller.Changed -= OnUpdateStateChanged;
            ShowAvailableUpdate();
        }
    }

    /// <summary>
    /// Shows the "update available" link while the controller knows of a newer version (found by its startup
    /// and daily checks, so a version the user skipped is not offered again here).
    /// </summary>
    private void OnUpdateStateChanged(object? sender, EventArgs e) => ShowAvailableUpdate();

    private void ShowAvailableUpdate()
    {
        if (_updateCheckService?.Controller.Available is not { } update)
        {
            return;
        }

        UpdateLinkRun.Text = Loc.Format("About.UpdateAvailable", update.Version.ToString(includeMetadata: false));
        UpdateStatusText.Visibility = Visibility.Visible;
    }

    private void UpdateLink_Click(object sender, RoutedEventArgs e)
    {
        if (_updateCheckService == null)
        {
            return;
        }

        if (UpdateDialog.ShowFor(_updateCheckService, this))
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
