using System.Reflection;
using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.LastCheck;

namespace ManagedDrive.App.Services;

/// <summary>
/// Checks the GitHub Releases API for a newer published version than the one currently running,
/// gated by <see cref="AppConfiguration.AutoCheckForUpdates"/> and a once-per-<see cref="CheckInterval"/>
/// throttle (both enforced by GitHubReleaseUpdater itself via <see cref="SettingsLastCheckStore"/>).
/// Runs at startup (silent, fire-and-forget; <see cref="App"/> then prompts the user or shows a tray
/// balloon) and automatically whenever <see cref="AboutDialog"/> is opened. Also hands out the
/// <see cref="UpdateInstaller"/> that downloads and starts the installer once the user agrees.
/// </summary>
/// <param name="settings">Settings file holding the last-check time and the skipped version.</param>
/// <param name="trayIconController">Shows the "update available" balloon when no window can show a prompt.</param>
/// <param name="getDisks">Gives the currently mounted disks, used to detect a TEMP directory on a RAM disk.</param>
public sealed class UpdateCheckService(SettingsStore settings, TrayIconController trayIconController, Func<IEnumerable<DiskViewModel>> getDisks)
{
    /// <summary>
    /// GitHub owner of the repository releases are read from.
    /// </summary>
    private const string RepoOwner = "coldhighsun";

    /// <summary>
    /// GitHub repository releases are read from.
    /// </summary>
    private const string RepoName = "ManagedDrive";

    /// <summary>
    /// Matches the Inno Setup installer asset (<c>ManagedDrive-Setup-v1.2.3.exe</c>) and not the portable zip.
    /// </summary>
    internal const string InstallerAssetPattern = "ManagedDrive-Setup-{tag}.exe";

    /// <summary>
    /// Minimum time between two automatic checks.
    /// </summary>
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
    /// Directory downloaded installers are stored in. Deliberately not under <c>%TEMP%</c>: the user's TEMP
    /// can point at a ManagedDrive RAM disk, which the installer refuses to run from and which would
    /// be gone after a restart.
    /// </summary>
    public static string GetDownloadDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManagedDrive", "updates");

    /// <summary>
    /// Runs a check respecting <see cref="AppConfiguration.AutoCheckForUpdates"/> and the daily
    /// throttle. Never throws; intended to be called fire-and-forget from application startup.
    /// </summary>
    /// <returns>The newer release, or <see langword="null"/> when there is none, the check was skipped or throttled, or it failed.</returns>
    public async Task<UpdateInfo?> CheckOnStartupAsync(AppConfiguration config)
    {
        if (!config.AutoCheckForUpdates)
        {
            return null;
        }

        try
        {
            using var updater = CreateUpdater(new SettingsLastCheckStore(settings));

            var result = await updater.CheckForUpdateAsync();
            return result is { Success: true, Throttled: false, IsUpdateAvailable: true } ? ToUpdateInfo(result) : null;
        }
        catch (FormatException)
        {
            // The running version is not valid SemVer (e.g. an untagged dev build), so there is nothing to
            // compare against. Startup checks must never surface an error to the user.
            return null;
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
            if (!result.Success)
            {
                return (false, null);
            }

            return (true, result.IsUpdateAvailable ? ToUpdateInfo(result) : null);
        }
        catch (FormatException)
        {
            // The running version is not valid SemVer (e.g. an untagged dev build): the check cannot run.
            return (false, null);
        }
    }

    /// <summary>
    /// Remembers that the user does not want to be prompted about <paramref name="info"/> again. Startup
    /// checks then stay silent until a newer version than this one is published.
    /// </summary>
    /// <param name="info">The update to skip.</param>
    public Task SkipVersionAsync(UpdateInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return info.Check.Update is { } update
            ? new SettingsLastCheckStore(settings).SetSkippedVersionAsync(update.Version)
            : Task.CompletedTask;
    }

    /// <summary>
    /// Creates the installer that downloads and starts the setup program for an update. Fails before
    /// downloading anything when GitHub reports no SHA-256 for the asset, so an unverifiable installer is
    /// never run with administrator rights.
    /// </summary>
    public UpdateInstaller CreateInstaller() =>
        new(CreateUpdater(lastCheckStore: null, requireChecksum: true), GetDownloadDirectory(), PrepareForInstall);

    /// <summary>
    /// Whether the user's TEMP directory currently points at a mounted ManagedDrive RAM disk, which the
    /// installer refuses to run with (it would vanish while the disk is unmounted for the update).
    /// </summary>
    public bool IsTempOnRamDisk() => TempDirCompatChecker.IsTempOnAnyDisk(getDisks());

    /// <summary>
    /// Shows a tray balloon announcing <paramref name="info"/> — used when no window is visible to prompt
    /// from, since a modal dialog would otherwise pop up uninvited over whatever the user is doing.
    /// </summary>
    /// <param name="info">The update to announce.</param>
    public void NotifyUpdateAvailable(UpdateInfo info) =>
        trayIconController.ShowBalloonTip(
            "ManagedDrive",
            Loc.Format("Update.BalloonBody", info.Version),
            System.Windows.Forms.ToolTipIcon.Info);

    /// <summary>
    /// Builds a <see cref="ReleaseUpdater"/> for this repository.
    /// </summary>
    /// <param name="lastCheckStore">Throttling and skipped-version store, or <see langword="null"/> for none.</param>
    /// <param name="requireChecksum">Whether a download without a known SHA-256 must be refused.</param>
    private static ReleaseUpdater CreateUpdater(ILastCheckStore? lastCheckStore, bool requireChecksum = false) => new(new()
    {
        Owner = RepoOwner,
        Repo = RepoName,
        CurrentVersion = GetRunningVersion(),
        IncludePrerelease = false,
        AssetSelector = new PatternAssetSelector(InstallerAssetPattern),
        RequireChecksum = requireChecksum,
        LastCheckStore = lastCheckStore,
        MinimumCheckInterval = lastCheckStore is null ? null : CheckInterval,
    });

    /// <summary>
    /// Converts a successful check with an update into an <see cref="UpdateInfo"/>.
    /// </summary>
    /// <returns>The update, or <see langword="null"/> when the release has no page URL to link to.</returns>
    private static UpdateInfo? ToUpdateInfo(UpdateCheckResult result) =>
        result.Update is { Release.HtmlUrl: { } url } update
            ? new(update.Version.ToString(), new(url), update.Release.Body, update.SelectedAsset?.Size, result)
            : null;

    /// <summary>
    /// Runs right before the installer starts: resets a TEMP directory that points at a RAM disk, as the
    /// installer would otherwise abort without telling the user (it only logs when running silently). The
    /// reset has to happen before the installer's UAC prompt, so the returned action puts the previous TEMP
    /// back for when the installer then does not start.
    /// </summary>
    /// <returns>An action restoring the previous TEMP, or <see langword="null"/> when nothing was changed.</returns>
    /// <exception cref="InvalidOperationException">TEMP is on a RAM disk and could not be reset.</exception>
    private Action? PrepareForInstall()
    {
        if (!IsTempOnRamDisk())
        {
            return null;
        }

        var previous = TempDirResetService.Capture();
        if (!TempDirResetService.Reset())
        {
            throw new InvalidOperationException(Loc.Get("Update.TempResetFailed"));
        }

        return previous is null ? null : () => TempDirResetService.Restore(previous);
    }
}
