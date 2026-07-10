using System.Reflection;
using System.Windows.Navigation;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="AboutDialog"/>.
/// </summary>
public partial class AboutDialog
{
    private const string GitHubUrl = "https://github.com/coldhighsun/ManagedDrive";
    private const string WinFspUrl = "https://winfsp.dev/";

    public AboutDialog()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? string.Empty;
        var plusIndex = version.IndexOf('+');
        if (plusIndex > 0)
        {
            version = version[..plusIndex];
        }

        VersionText.Text = version;
        GitHubLink.NavigateUri = new(GitHubUrl);
        WinFspLink.NavigateUri = new(WinFspUrl);
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}