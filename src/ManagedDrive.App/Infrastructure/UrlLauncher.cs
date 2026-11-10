using System.ComponentModel;

namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Opens web links in the user's default browser, refusing anything that is not a plain http or https address.
/// </summary>
public static class UrlLauncher
{
    /// <summary>
    /// Logs launch failures.
    /// </summary>
    private static readonly ILogger Logger = AppLog.CreateLogger(typeof(UrlLauncher));

    /// <summary>
    /// Tells whether <paramref name="url"/> is an absolute http or https address that may be handed to the shell.
    /// </summary>
    /// <param name="url">The address to check.</param>
    /// <returns><see langword="true"/> for an absolute http/https URL; otherwise <see langword="false"/>.</returns>
    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// Opens <paramref name="url"/> in the default browser when it is allowed.
    /// </summary>
    /// <param name="url">The address to open.</param>
    /// <returns><see langword="true"/> when the browser was started; <see langword="false"/> when the URL was refused or the launch failed.</returns>
    public static bool TryOpen(string? url)
    {
        if (!IsAllowed(url))
        {
            Logger.LogWarning("Refused to open a non-http(s) URL: {Url}", url);
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Could not open {Url}", url);
            return false;
        }
    }
}
