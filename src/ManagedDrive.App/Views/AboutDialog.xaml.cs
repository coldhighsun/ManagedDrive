using System.Windows.Navigation;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="AboutDialog"/>.
/// </summary>
public partial class AboutDialog
{
    private const string GitHubUrl = "https://github.com/coldhighsun/ManagedDrive";
    private const string SharpCompressUrl = "https://github.com/adamhathcock/sharpcompress";
    private const string WinFspUrl = "https://winfsp.dev/";
    private readonly UpdateCheckService? _updateCheckService;

    public AboutDialog(UpdateCheckService? updateCheckService = null)
    {
        InitializeComponent();
        _updateCheckService = updateCheckService;

        VersionText.Text = UpdateCheckService.GetRunningVersion();
        GitHubLink.NavigateUri = new(GitHubUrl);
        WinFspLink.NavigateUri = new(WinFspUrl);
        SharpCompressLink.NavigateUri = new(SharpCompressUrl);

        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        if (_updateCheckService == null)
        {
            return;
        }

        var (result, info) = await _updateCheckService.CheckSilentlyAsync();
        if (result != UpdateCheckResult.UpdateAvailable || info == null)
        {
            return;
        }

        UpdateLink.NavigateUri = info.ReleaseUrl;
        UpdateLinkRun.Text = Loc.Format("About.UpdateAvailable", info.Version);
        UpdateStatusText.Visibility = Visibility.Visible;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}