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

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders =
        {
            UserAgent = { new("ManagedDrive", GetRunningVersion()) },
            Accept = { new("application/vnd.github+json") },
        },
    };

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

    /// <summary>
    /// Parses a (possibly <c>v</c>-prefixed) version string tolerantly, splitting off a SemVer
    /// <c>-prerelease</c> suffix (and ignoring any <c>+build</c> metadata). Unlike
    /// <see cref="TryParseVersion"/>, a prerelease string such as <c>1.9.0-alpha.1</c> parses
    /// successfully: <paramref name="version"/> receives the numeric core and
    /// <paramref name="isPrerelease"/> is set to <see langword="true"/>.
    /// </summary>
    private static bool TryParseRunningVersion(string versionText, out Version? version, out bool isPrerelease)
    {
        var trimmed = versionText.StartsWith('v') || versionText.StartsWith('V')
            ? versionText[1..]
            : versionText;

        // Strip +build metadata first, then split off the -prerelease part.
        var plusIndex = trimmed.IndexOf('+');
        if (plusIndex >= 0)
        {
            trimmed = trimmed[..plusIndex];
        }

        var dashIndex = trimmed.IndexOf('-');
        isPrerelease = dashIndex >= 0;
        var core = isPrerelease ? trimmed[..dashIndex] : trimmed;

        return Version.TryParse(core, out version);
    }

    /// <summary>
    /// Determines whether the formal release <paramref name="latestFormalVersion"/> is a newer
    /// version than the (possibly prerelease) <paramref name="runningVersion"/>. A prerelease
    /// build precedes the formal release with the same numeric core (e.g. <c>1.9.0-alpha.1</c> is
    /// older than <c>1.9.0</c>). Returns <see langword="false"/> when either version can't be parsed.
    /// </summary>
    public static bool IsNewerFormalRelease(string runningVersion, string latestFormalVersion)
    {
        if (!TryParseRunningVersion(runningVersion, out var running, out var runningIsPrerelease) ||
            !TryParseRunningVersion(latestFormalVersion, out var latest, out _))
        {
            return false;
        }

        // Normalize to three fields (build defaulting to 0) so 1.9 and 1.9.0 compare equal —
        // Version.CompareTo otherwise treats an unspecified component as -1.
        var runningCore = new Version(running!.Major, running.Minor, Math.Max(running.Build, 0));
        var latestCore = new Version(latest!.Major, latest.Minor, Math.Max(latest.Build, 0));

        var coreComparison = latestCore.CompareTo(runningCore);
        if (coreComparison > 0)
        {
            return true;
        }

        // Same numeric core: the formal release outranks its own prereleases.
        return coreComparison == 0 && runningIsPrerelease;
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
            release = await _httpClient.GetFromJsonAsync<GitHubReleaseDto>(ReleasesApiUrl, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (UpdateCheckResult.Error, null);
        }

        settings.Save(settings.Load() with
        {
            LastUpdateCheckUtc = DateTimeOffset.UtcNow
        });

        if (release == null)
        {
            return (UpdateCheckResult.UpToDate, null);
        }

        if (!TryParseVersion(release.TagName, out var latest))
        {
            // Not a formal x.x.x release (e.g. a prerelease/build-suffixed tag) — nothing eligible
            // to offer, not a failure.
            return (UpdateCheckResult.UpToDate, null);
        }

        var latestVersionText = latest!.ToString();
        if (!IsNewerFormalRelease(GetRunningVersion(), latestVersionText))
        {
            return (UpdateCheckResult.UpToDate, null);
        }

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
        [property: JsonPropertyName("html_url")] string HtmlUrl);
}