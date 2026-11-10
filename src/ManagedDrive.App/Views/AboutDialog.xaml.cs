using System.Windows.Navigation;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="AboutDialog"/>.
/// </summary>
public partial class AboutDialog
{
    private const string GitHubUrl = UpdateCheckService.RepositoryUrl;
    private const string ThirdPartyNoticesUrl = GitHubUrl + "/blob/main/docs/THIRD-PARTY-NOTICES.md";

    /// <summary>
    /// The GitHub Sponsors page the "support the project" link opens.
    /// </summary>
    private const string DonateUrl = UpdateCheckService.SponsorsUrl;

    /// <summary>
    /// The update service whose automatic check result this dialog shows.
    /// </summary>
    private readonly UpdateCheckService? _updateCheckService;

    /// <summary>
    /// Initializes a new instance of the <see cref="AboutDialog"/> class and shows the update the service's automatic check has found.
    /// </summary>
    public AboutDialog(UpdateCheckService? updateCheckService = null)
    {
        InitializeComponent();
        Loaded += (_, _) => DialogPlacement.BringToFrontWhenUnowned(this);
        _updateCheckService = updateCheckService;

        VersionText.Text = UpdateCheckService.GetRunningVersion();
        GitHubLink.NavigateUri = new(GitHubUrl);
        ThirdPartyNoticesLink.NavigateUri = new(ThirdPartyNoticesUrl);
        DonateLink.NavigateUri = new(DonateUrl);

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

    /// <summary>
    /// Opens the clicked link in the default browser and tells the user when that is not possible.
    /// </summary>
    /// <param name="sender">The clicked hyperlink.</param>
    /// <param name="e">The navigation request carrying the target address.</param>
    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        var url = e.Uri.AbsoluteUri;
        if (!UrlLauncher.TryOpen(url))
        {
            AppMessageBox.Show(
                Loc.Format("About.OpenLinkFailed", url),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        e.Handled = true;
    }
}
