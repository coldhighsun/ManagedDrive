using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManagedDrive.App.Services;

/// <summary>
/// Result of a single update check.
/// </summary>
public enum UpdateCheckResult
{
    UpdateAvailable,
    UpToDate,
    Skipped,
    Error,
}

/// <summary>
/// Checks the GitHub Releases API for a newer published version than the one currently running,
/// gated by <see cref="AppConfiguration.AutoCheckForUpdates"/> and a once-per-<see cref="CheckInterval"/>
/// throttle. Runs at startup (silent, fire-and-forget, tray balloon + dialog on a hit) and
/// automatically whenever <see cref="AboutDialog"/> is opened (silent — just an inline link).
/// </summary>
public sealed class UpdateCheckService(SettingsStore settings, TrayIconController trayIconController, Func<Window?> ownerWindowProvider)
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/coldhighsun/ManagedDrive/releases/latest";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders =
        {
            UserAgent = { new("ManagedDrive", GetRunningVersion()) },
            Accept = { new("application/vnd.github+json") },
        },
    };

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
    /// throttle, showing a tray balloon (and, if the main window is visible, the update dialog)
    /// when a newer version is found. Never throws; intended to be called fire-and-forget from
    /// application startup.
    /// </summary>
    public async Task CheckOnStartupAsync(AppConfiguration config)
    {
        try
        {
            var (result, info) = await CheckCoreAsync(forceCheck: false, config, CancellationToken.None);
            if (result == UpdateCheckResult.UpdateAvailable && info != null)
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
    /// <see cref="AboutDialog"/>, which renders the result as an inline link itself. Bypasses the
    /// auto-check toggle, the daily throttle, and any previously skipped version, since the user
    /// explicitly opened the dialog and expects a fresh answer.
    /// </summary>
    public async Task<(UpdateCheckResult Result, UpdateInfo? Info)> CheckSilentlyAsync(CancellationToken ct = default)
    {
        try
        {
            return await CheckCoreAsync(forceCheck: true, settings.Load(), ct);
        }
        catch
        {
            return (UpdateCheckResult.Error, null);
        }
    }

    /// <summary>
    /// Parses a (possibly <c>v</c>-prefixed) formal <c>x.x.x</c> version string. Deliberately does
    /// <em>not</em> strip a <c>-prerelease</c>/<c>+build</c> suffix — a tag like
    /// <c>v1.6.0-alpha.0.1</c> is not a formal release and must fail to parse here, so
    /// <see cref="CheckCoreAsync"/> can ignore it entirely rather than offering it as an update.
    /// </summary>
    private static bool TryParseVersion(string tagOrVersion, out Version? version)
    {
        var trimmed = tagOrVersion.StartsWith('v') || tagOrVersion.StartsWith('V')
            ? tagOrVersion[1..]
            : tagOrVersion;

        return Version.TryParse(trimmed, out version);
    }

    private async Task<(UpdateCheckResult Result, UpdateInfo? Info)> CheckCoreAsync(bool forceCheck, AppConfiguration config, CancellationToken ct)
    {
        if (!forceCheck)
        {
            if (!config.AutoCheckForUpdates)
            {
                return (UpdateCheckResult.Skipped, null);
            }

            if (config.LastUpdateCheckUtc is { } last && DateTimeOffset.UtcNow - last < CheckInterval)
            {
                return (UpdateCheckResult.Skipped, null);
            }
        }

        GitHubReleaseDto? release;
        try
        {
            release = await s_httpClient.GetFromJsonAsync<GitHubReleaseDto>(ReleasesApiUrl, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (UpdateCheckResult.Error, null);
        }

        settings.Save(settings.Load() with
        {
            LastUpdateCheckUtc = DateTimeOffset.UtcNow
        });

        if (release == null || release.Prerelease || release.Draft)
        {
            return (UpdateCheckResult.UpToDate, null);
        }

        if (!TryParseVersion(release.TagName, out var latest))
        {
            // Not a formal x.x.x release (e.g. a prerelease/build-suffixed tag) — nothing eligible
            // to offer, not a failure.
            return (UpdateCheckResult.UpToDate, null);
        }

        if (!TryParseVersion(GetRunningVersion(), out var running))
        {
            return (UpdateCheckResult.Error, null);
        }

        if (latest!.CompareTo(running) <= 0)
        {
            return (UpdateCheckResult.UpToDate, null);
        }

        var latestVersionText = latest.ToString();
        if (!forceCheck && string.Equals(config.SkippedVersion, latestVersionText, StringComparison.Ordinal))
        {
            return (UpdateCheckResult.Skipped, null);
        }

        return (UpdateCheckResult.UpdateAvailable, new UpdateInfo(latestVersionText, new(release.HtmlUrl)));
    }

    private void NotifyUpdateAvailable(UpdateInfo info)
    {
        trayIconController.ShowBalloonTip(
            "ManagedDrive",
            Loc.Format("Update.BalloonBody", info.Version),
            System.Windows.Forms.ToolTipIcon.Info);

        if (ownerWindowProvider() is not { IsVisible: true })
        {
            return;
        }

        var dialog = new UpdateAvailableDialog(info);
        if (ownerWindowProvider() is { } owner)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();

        if (dialog.Action == UpdateDialogAction.Skip)
        {
            settings.Save(settings.Load() with
            {
                SkippedVersion = info.Version
            });
        }
    }

    private sealed record GitHubReleaseDto(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("draft")] bool Draft);
}