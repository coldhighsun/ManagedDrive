using System.Reflection;
using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.AutoUpdate;
using GitHubReleaseUpdater.Installation;
using GitHubReleaseUpdater.Versioning;

namespace ManagedDrive.App.Services;

/// <summary>
/// ManagedDrive's side of the self-update: the checking, announcing, downloading and installer start are all
/// done by GitHubReleaseUpdater's <see cref="UpdateController"/> (automatic check at startup and then daily,
/// throttled and skip-aware through <see cref="SettingsLastCheckStore"/>, manual check for the About dialog,
/// SHA-256-verified download, Inno Setup start); this class is its <see cref="IUpdatePresenter"/> and only adds
/// ManagedDrive's own policy: where the installer is stored, the step that must run before it starts, and the
/// events the UI listens to. It does not exit the app: the installer asks the running app to exit through
/// <c>mdrive.exe exit</c> (saving every mounted disk first) and restarts it once installation finishes.
/// </summary>
public sealed class UpdateCheckService : IUpdatePresenter, IDisposable
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
    /// Web address of the repository.
    /// </summary>
    internal const string RepositoryUrl = $"https://github.com/{RepoOwner}/{RepoName}";

    /// <summary>
    /// Releases page, for when a release carries no address of its own.
    /// </summary>
    internal const string ReleasesUrl = RepositoryUrl + "/releases/latest";

    /// <summary>
    /// Matches the Inno Setup installer asset (<c>ManagedDrive-Setup-v1.2.3.exe</c>) and not the portable zip.
    /// </summary>
    internal const string InstallerAssetPattern = "ManagedDrive-Setup-{tag}.exe";

    /// <summary>
    /// Minimum time between two automatic checks.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Wait before the first automatic check, so it does not compete with the app starting up.
    /// </summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    private readonly ReleaseUpdater _updater;
    private readonly Func<bool> _isTempOnRamDisk;

    /// <summary>
    /// What undoes the preparation of the installation in progress, or <see langword="null"/> when it changed nothing.
    /// </summary>
    private Action? _undoPrepare;

    /// <summary>
    /// Whether the controller has started the installer of the installation in progress (the state does not
    /// tell: it is not kept at <see cref="UpdateState.Installing"/> once the installer runs).
    /// </summary>
    private bool _installerStarted;

    /// <summary>
    /// Initializes the service around an already configured updater.
    /// </summary>
    /// <param name="updater">The updater to check, download and launch with; disposed with this instance.</param>
    /// <param name="downloadDirectory">Directory the installer is downloaded into.</param>
    /// <param name="isAutoCheckEnabled">Asked before every automatic check.</param>
    /// <param name="isTempOnRamDisk">Whether the user's TEMP currently points at a ManagedDrive RAM disk.</param>
    /// <param name="prepareForInstall">
    /// Runs after the download is verified, right before the installer starts, but only while
    /// <paramref name="isTempOnRamDisk"/> says so. It returns an action that undoes what it changed, which is run when
    /// the installer then does not start (cancelled, failed, UAC declined) so the user is left as before. Throwing
    /// aborts the update; it must leave nothing changed then.
    /// </param>
    /// <param name="onError">Receives exceptions that have nowhere else to go (presenter, last-check store).</param>
    internal UpdateCheckService(
        ReleaseUpdater updater,
        string downloadDirectory,
        Func<bool> isAutoCheckEnabled,
        Func<bool> isTempOnRamDisk,
        Func<Action> prepareForInstall,
        Action<Exception>? onError = null)
    {
        _updater = updater;
        _isTempOnRamDisk = isTempOnRamDisk;
        Controller = new(updater, this, new()
        {
            InitialDelay = InitialDelay,
            CheckInterval = CheckInterval,
            IsAutoCheckEnabled = isAutoCheckEnabled,
            DownloadDirectory = downloadDirectory,
            InstallerLaunchOptions = InstallerLaunchOptions.InnoSetupSilent,
            CleanDownloadDirectory = true,
            AskToInstallAfterManualCheck = false,
            BeforeInstallAsync = _ =>
            {
                if (isTempOnRamDisk())
                {
                    _undoPrepare = prepareForInstall();
                }

                return Task.CompletedTask;
            },
            BeforeInstallMustSucceed = true,
            InstallNotStartedAsync = _ =>
            {
                var undo = _undoPrepare;
                _undoPrepare = null;
                undo?.Invoke();
                return Task.CompletedTask;
            },
            OnError = onError,
        });
    }

    /// <summary>
    /// Raised when an automatic check finds a version, once per version.
    /// </summary>
    public event Action<AvailableUpdate>? UpdateAnnounced;

    /// <summary>
    /// The update flow: its <see cref="UpdateController.State"/>, <see cref="UpdateController.Available"/> and
    /// <see cref="UpdateController.Changed"/> tell what has been found.
    /// </summary>
    public UpdateController Controller { get; }

    /// <summary>
    /// Creates the service for the running app.
    /// </summary>
    /// <param name="settings">Settings file holding the last-check time, the skipped version and the auto-check switch.</param>
    /// <param name="getDisks">Gives the currently mounted disks, used to detect a TEMP directory on a RAM disk.</param>
    /// <returns>The service, or <see langword="null"/> when the running version is not valid SemVer (e.g. an untagged dev build), so there is nothing to compare against.</returns>
    public static UpdateCheckService? Create(SettingsStore settings, Func<IEnumerable<DiskViewModel>> getDisks)
    {
        var version = GetRunningVersion();
        if (!SemanticVersion.TryParse(version, out _))
        {
            return null;
        }

        var updater = new ReleaseUpdater(new()
        {
            Owner = RepoOwner,
            Repo = RepoName,
            CurrentVersion = version,
            AssetSelector = new PatternAssetSelector(InstallerAssetPattern),
            LastCheckStore = new SettingsLastCheckStore(settings),
            MinimumCheckInterval = CheckInterval,
        });

        bool IsTempOnRamDisk() => TempDirCompatChecker.IsTempOnAnyDisk(getDisks());

        return new(
            updater,
            DownloadDirectory,
            () => settings.Load().AutoCheckForUpdates,
            IsTempOnRamDisk,
            PrepareForInstall,
            ex => AppLog.CreateLogger<UpdateCheckService>().LogWarning(ex, "Update flow reported an error."));
    }

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
    /// be gone after a restart. Dedicated to updates, so the controller may clean it.
    /// </summary>
    private static string DownloadDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManagedDrive", "updates");

    /// <summary>
    /// Whether the user's TEMP directory currently points at a mounted ManagedDrive RAM disk, which the
    /// installer refuses to run with (it would vanish while the disk is unmounted for the update).
    /// </summary>
    public bool IsTempOnRamDisk() => _isTempOnRamDisk();

    /// <summary>
    /// Downloads, verifies and starts the installer of <see cref="UpdateController.Available"/> after the
    /// user agreed. TEMP is only touched once there is a verified installer to start, so a slow, cancelled or
    /// failed download never leaves it changed. Failures are not thrown but left in <see cref="LastInstallError"/>.
    /// Only one installation can run at a time.
    /// </summary>
    /// <param name="cancellationToken">Cancels the download; the installer is never started once cancelled.</param>
    /// <returns>
    /// <see langword="true"/> when the installer was started; <see langword="false"/> when it was not, either
    /// because of a failure (see <see cref="LastInstallError"/>) or because the user declined its
    /// elevation (UAC) prompt, in which case nothing changed and the app keeps running.
    /// </returns>
    /// <exception cref="InvalidOperationException">An installation is already running, or there is nothing to install.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<bool> InstallAsync(CancellationToken cancellationToken)
    {
        if (Controller.IsBusy)
        {
            throw new InvalidOperationException("An installation is already running.");
        }

        if (!Controller.CanInstall)
        {
            throw new InvalidOperationException("There is no update to install.");
        }

        _installerStarted = false;
        await Controller.InstallAsync(cancellationToken);
        return _installerStarted;
    }

    /// <summary>
    /// Why the last download or installation failed, or <see langword="null"/> when it did not (a declined
    /// elevation prompt is not a failure). Cleared by the controller's next change of state.
    /// </summary>
    public Exception? LastInstallError => Controller.LastFailure is { Stage: not UpdateFailureStage.Check } failure ? failure.Error : null;

    /// <summary>
    /// Disposes the underlying <see cref="ReleaseUpdater"/>.
    /// </summary>
    public void Dispose() => _updater.Dispose();

    void IUpdatePresenter.Announce(AvailableUpdate update) => UpdateAnnounced?.Invoke(update);

    ValueTask<bool> IUpdatePresenter.ConfirmInstallAsync(AvailableUpdate update, SemanticVersion current) =>
        ValueTask.FromResult(true);

    void IUpdatePresenter.InstallerStarted(AvailableUpdate update)
    {
        _installerStarted = true;
        _undoPrepare = null;
    }

    /// <summary>
    /// Resets a TEMP directory that points at a RAM disk, as the installer would otherwise abort without telling
    /// the user (it only logs when running silently). The reset has to happen before the installer's UAC prompt,
    /// so the returned action puts the previous TEMP back for when the installer then does not start.
    /// </summary>
    /// <returns>An action restoring the previous TEMP.</returns>
    /// <exception cref="InvalidOperationException">TEMP could not be reset; whatever the attempt changed is put back first.</exception>
    private static Action PrepareForInstall()
    {
        var previous = TempDirResetService.Capture();
        if (!TempDirResetService.Reset())
        {
            if (previous is not null)
            {
                TempDirResetService.Restore(previous);
            }

            throw new InvalidOperationException(Loc.Get("Update.TempResetFailed"));
        }

        return () =>
        {
            if (previous is not null)
            {
                TempDirResetService.Restore(previous);
            }
        };
    }
}
