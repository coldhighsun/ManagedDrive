using System.Diagnostics;
using System.Reflection;
using System.Windows.Navigation;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="AboutDialog"/>.
/// </summary>
public partial class AboutDialog
{
    private const string GitHubUrl = "https://github.com/coldhighsun/ManagedDrive";

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
        GitHubLink.NavigateUri = new Uri(GitHubUrl);
    }

    private void GitHubLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
