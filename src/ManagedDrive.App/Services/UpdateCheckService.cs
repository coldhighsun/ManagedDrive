using System.Reflection;
using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.Versioning;

namespace ManagedDrive.App.Services;

/// <summary>
/// Checks the GitHub Releases API for a newer published version than the one currently running,
/// gated by <see cref="AppConfiguration.AutoCheckForUpdates"/> and a once-per-<see cref="CheckInterval"/>
/// throttle (both enforced by GitHubReleaseUpdater itself via <see cref="SettingsLastCheckStore"/>).
/// Runs at startup (silent, fire-and-forget, tray balloon on a hit) and automatically whenever
/// <see cref="AboutDialog"/> is opened (silent — just an inline link).
/// </summary>
public sealed class UpdateCheckService(SettingsStore settings, TrayIconController trayIconController)
{
    private const string RepoOwner = "coldhighsun";
    private const string RepoName = "ManagedDrive";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Strips the <c>+&lt;git-hash&gt;</c> suffix MinVer appends to non-tagged builds from the
    /// assembly's informational version.
    /// </summary>
    public static string GetRunningVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? string.Empty;
        var plusIndex = version.IndexOf('+');
        return plusIndex > 0 ? version[..plusIndex] : version;
    }

    /// <summary>
    /// Runs a check respecting <see cref="AppConfiguration.AutoCheckForUpdates"/> and the daily
    /// throttle, showing a tray balloon when a newer version is found. Never throws; intended to
    /// be called fire-and-forget from application startup.
    /// </summary>
    public async Task CheckOnStartupAsync(AppConfiguration config)
    {
        if (!config.AutoCheckForUpdates)
        {
            return;
        }

        try
        {
            var lastCheckStore = new SettingsLastCheckStore(settings);
            using var updater = CreateUpdater(lastCheckStore);

            var result = await updater.CheckForUpdateAsync();
            if (!result.Throttled && result.IsUpdateAvailable && result.Update != null && ToUpdateInfo(result.Update) is { } info)
            {
                NotifyUpdateAvailable(info);
            }
        }
        catch
        {
            // Startup checks must never surface an error to the user.
        }
    }

    /// <summary>
    /// Checks for a newer release without showing any tray balloon or dialog — used by
    /// <see cref="AboutDialog"/>, which renders the result as an inline link itself. Bypasses both
    /// the daily throttle and any previously skipped version, since the user explicitly asked for
    /// a fresh check — while still recording the check time via the shared
    /// <see cref="SettingsLastCheckStore"/>, same as the throttled path.
    /// </summary>
    /// <returns>
    /// <c>Success</c> is <see langword="false"/> only when the check itself failed (network/API
    /// error). A successful check with no update yields <c>(true, null)</c>.
    /// </returns>
    public async Task<(bool Success, UpdateInfo? Info)> CheckSilentlyAsync(CancellationToken ct = default)
    {
        try
        {
            using var updater = CreateUpdater(new SettingsLastCheckStore(settings));

            var result = await updater.CheckForUpdateAsync(bypassSkippedVersion: true, bypassThrottle: true, ct);

            return result.IsUpdateAvailable && result.Update != null
                ? (true, ToUpdateInfo(result.Update))
                : (true, null);
        }
        catch (UpdaterException)
        {
            return (false, null);
        }
    }

    private static ReleaseUpdater CreateUpdater(SettingsLastCheckStore lastCheckStore) => new(new()
    {
        Owner = RepoOwner,
        Repo = RepoName,
        CurrentVersion = GetRunningVersion(),
        IncludePrerelease = false,
        LastCheckStore = lastCheckStore,
        MinimumCheckInterval = CheckInterval,
    });

    private static UpdateInfo? ToUpdateInfo(AvailableUpdate update) =>
        update.Release.HtmlUrl == null ? null : new(update.Version.ToString(), new(update.Release.HtmlUrl));

    private void NotifyUpdateAvailable(UpdateInfo info) =>
        trayIconController.ShowBalloonTip(
            "ManagedDrive",
            Loc.Format("Update.BalloonBody", info.Version),
            System.Windows.Forms.ToolTipIcon.Info);
}
