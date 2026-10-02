using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Download;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.Installation;

namespace ManagedDrive.App.Services;

/// <summary>
/// Downloads the installer of an <see cref="UpdateInfo"/> and starts it. The download, verification and
/// process start are all done by GitHubReleaseUpdater; this class only adds ManagedDrive's own policy:
/// where the installer is stored, the Inno Setup arguments, and the step that must run right before the
/// installer starts. It does not exit the app: the installer asks the running app to exit through
/// <c>mdrive.exe exit</c> (saving every mounted disk first) and restarts it once installation finishes.
/// </summary>
/// <param name="updater">The updater that downloads and launches the installer; disposed with this instance.</param>
/// <param name="downloadDirectory">Directory the installer is downloaded into.</param>
/// <param name="prepareForInstall">
/// Runs after the download has been verified and right before the installer starts; throwing aborts the
/// update without starting the installer. May return an action that undoes what it changed, which is run
/// when the installer then does not start (UAC declined or launch failure) so the user is left as before.
/// </param>
public sealed class UpdateInstaller(ReleaseUpdater updater, string downloadDirectory, Func<Action?> prepareForInstall) : IDisposable
{
    /// <summary>
    /// Downloads and verifies the installer for <paramref name="info"/>, then starts it silently.
    /// </summary>
    /// <param name="info">The update to install; <see cref="UpdateInfo.CanInstall"/> must be <see langword="true"/>.</param>
    /// <param name="progress">Receives download progress.</param>
    /// <param name="cancellationToken">Cancels the download; the installer is never started once cancelled.</param>
    /// <returns>
    /// <see langword="true"/> when the installer was started; <see langword="false"/> when the user declined its
    /// elevation (UAC) prompt, in which case nothing changed and the app keeps running.
    /// </returns>
    /// <exception cref="UpdaterException">Download, verification or process start failed.</exception>
    public async Task<bool> DownloadAndLaunchAsync(UpdateInfo info, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        Directory.CreateDirectory(downloadDirectory);
        DeleteStaleInstallers(info.Check.Update?.SelectedAsset?.Name);

        var download = await updater.DownloadAsync(info.Check, downloadDirectory, progress, cancellationToken);
        var undo = prepareForInstall();

        try
        {
            await updater.LaunchInstallerAsync(download, InstallerLaunchOptions.InnoSetupSilent, cancellationToken);
            return true;
        }
        catch (InstallerLaunchException ex) when (ex.IsUserCancelled)
        {
            undo?.Invoke();
            return false;
        }
        catch
        {
            undo?.Invoke();
            throw;
        }
    }

    /// <summary>
    /// Disposes the underlying <see cref="ReleaseUpdater"/>.
    /// </summary>
    public void Dispose() => updater.Dispose();

    /// <summary>
    /// Removes installers left in <see cref="downloadDirectory"/> by earlier updates, so superseded
    /// versions do not pile up. Partial downloads are kept so an interrupted download can resume.
    /// </summary>
    /// <param name="currentAssetName">Name of the installer about to be downloaded, which is kept.</param>
    private void DeleteStaleInstallers(string? currentAssetName)
    {
        foreach (var file in Directory.EnumerateFiles(downloadDirectory, "*.exe"))
        {
            if (string.Equals(Path.GetFileName(file), currentAssetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup: an installer that is still running or locked stays until next time.
            }
        }
    }
}
