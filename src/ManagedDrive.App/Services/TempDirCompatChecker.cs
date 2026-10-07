namespace ManagedDrive.App.Services;

/// <summary>
/// Checks and repairs the user-level TEMP/TMP directory's compatibility with WinFsp-mounted RAM
/// disks: the one-time startup check/warning, and the tray "Reset TEMP Dirs" action. Extracted
/// from <see cref="App"/>.
/// </summary>
public sealed class TempDirCompatChecker
{
    private readonly Func<Window?> _ownerWindowProvider;
    private readonly SettingsStore _settings;
    private readonly TrayIconController _trayIconController;

    /// <summary>
    /// Puts TEMP and TMP back for the tray action: to what they held before they were pointed into a
    /// RAM disk, so the recorded backups are used up too, or to the Windows defaults when nothing was recorded.
    /// </summary>
    private readonly Func<TempRestoreResult> _restoreTemp;

    /// <param name="settings">Used to persist <see cref="AppConfiguration.TempDirCompatWarningShown"/>.</param>
    /// <param name="trayIconController">Used to show the reset-result balloon tip.</param>
    /// <param name="ownerWindowProvider">Supplies the confirm dialog's owner window, or <c>null</c> if none is loaded.</param>
    /// <param name="restoreTemp">
    /// Restores TEMP and TMP for the tray action; <c>null</c> resets them to the Windows defaults.
    /// </param>
    public TempDirCompatChecker(
        SettingsStore settings,
        TrayIconController trayIconController,
        Func<Window?> ownerWindowProvider,
        Func<TempRestoreResult>? restoreTemp = null)
    {
        _settings = settings;
        _trayIconController = trayIconController;
        _ownerWindowProvider = ownerWindowProvider;
        _restoreTemp = restoreTemp ?? (() => TempDirResetService.Reset() ? TempRestoreResult.Restored : TempRestoreResult.Failed);
    }

    /// <summary>
    /// Returns <c>true</c> when the user-level TEMP variable currently points into any of
    /// <paramref name="disks"/>.
    /// </summary>
    public static bool IsTempOnAnyDisk(IEnumerable<DiskViewModel> disks) =>
        IsTempOnAnyMountPoint(disks.Select(d => d.MountPoint));

    /// <summary>
    /// Returns <c>true</c> when the user-level TEMP variable currently points into any of
    /// <paramref name="mountPoints"/>. Takes plain strings so it can run off the UI thread, where
    /// the disk view models must not be touched.
    /// </summary>
    /// <param name="mountPoints">The mount points (drive letters or directories) to check.</param>
    /// <returns><c>true</c> if TEMP is set and lies on one of them.</returns>
    public static bool IsTempOnAnyMountPoint(IEnumerable<string> mountPoints)
    {
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        return !string.IsNullOrEmpty(userTemp)
            && IsPathOnAnyMountPoint(Environment.ExpandEnvironmentVariables(userTemp), mountPoints);
    }

    /// <summary>
    /// Whether <paramref name="path"/> lies on any of <paramref name="mountPoints"/>.
    /// </summary>
    /// <param name="path">The path to classify.</param>
    /// <param name="mountPoints">The mount points to compare with.</param>
    /// <returns><c>true</c> if at least one contains the path.</returns>
    internal static bool IsPathOnAnyMountPoint(string path, IEnumerable<string> mountPoints) =>
        mountPoints.Any(mountPoint => MountPointValidator.IsPathOnMountPoint(path, mountPoint));

    /// <summary>
    /// Returns the saved disk profile whose mount point (drive letter or directory) contains
    /// <paramref name="path"/>, preferring the most specific one.
    /// </summary>
    /// <param name="path">The path to look up, e.g. the expanded TEMP directory.</param>
    /// <param name="profiles">The saved disk profiles.</param>
    /// <returns>The matching profile, or <c>null</c> if <paramref name="path"/> is on none of them.</returns>
    internal static DiskProfile? FindProfileContainingPath(string path, IEnumerable<DiskProfile> profiles) =>
        profiles
            .Where(d => MountPointValidator.IsPathOnMountPoint(path, d.MountPoint))
            // Every match is a prefix of the normalized path, so the longest normalized mount point
            // is the innermost one; the raw strings may contain ".." or other non-canonical forms.
            // IsPathOnMountPoint already normalized each match successfully, so this can't throw.
            .MaxBy(d => MountPointValidator.NormalizeForPrefixCheck(d.MountPoint).Length);

    /// <summary>
    /// Runs the one-time startup check: if TEMP points into a saved disk profile's mount point
    /// that isn't set to auto-mount, resets TEMP and warns; if it matches an auto-mount profile,
    /// warns once (since elevated processes still can't reach WinFsp drives) and records that the
    /// warning was shown.
    /// </summary>
    public void CheckOnStartup(AppConfiguration config)
    {
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        if (string.IsNullOrEmpty(userTemp))
        {
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(userTemp);
        var matchingProfile = FindProfileContainingPath(expanded, config.Disks);

        if (matchingProfile == null)
        {
            return;
        }

        if (!matchingProfile.AutoMount)
        {
            // Disk is in profiles but not set to auto-mount — TEMP will be dangling after startup.
            TempDirResetService.Reset();

            MessageBox.Show(
                Loc.Format("Msg.StartupTempReset", expanded),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else
        {
            // Disk is auto-mount and will be available, but elevated processes (e.g. winget) still
            // cannot access user-session WinFsp drives. Warn once so the user is aware.
            if (!config.TempDirCompatWarningShown)
            {
                MessageBox.Show(
                    Loc.Format("Msg.StartupTempAutoMountWarning", expanded),
                    Loc.Get("Msg.SetTempDirWarningTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                // Not `config with { ... }`: config is the startup snapshot, and auto-mount has
                // saved newer disk profiles since.
                _settings.Update(current => current with
                {
                    TempDirCompatWarningShown = true
                });
            }
        }
    }

    /// <summary>
    /// Runs a post-auto-mount safety check: if TEMP still points into a saved disk profile's mount
    /// point, but no <em>currently live</em> disk actually occupies that mount point,
    /// TEMP is dangling — the disk it targeted failed to auto-mount (or an earlier crash skipped
    /// the reset that would normally happen on unmount/edit) and TEMP would otherwise be silently
    /// pointing at an inaccessible directory for the rest of the session. Resets TEMP and warns.
    /// </summary>
    /// <remarks>
    /// This is a safety net layered on top of <see cref="CheckOnStartup"/> and
    /// <c>MainViewModel.ResetTempIfPointingAt</c> (which already handles the common in-session
    /// auto-mount-failure case) — it catches whatever those miss, e.g. a profile that was removed
    /// or renamed while TEMP still referenced its old mount point. Must run <em>after</em>
    /// auto-mounting has finished, since it distinguishes "will mount shortly" from "failed to
    /// mount" by checking the live <paramref name="disks"/> collection.
    /// </remarks>
    public void CheckAfterAutoMount(AppConfiguration config, IEnumerable<DiskViewModel> disks)
    {
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        if (string.IsNullOrEmpty(userTemp))
        {
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(userTemp);
        var isKnownProfile = FindProfileContainingPath(expanded, config.Disks) is not null;
        if (!isKnownProfile || IsTempOnAnyDisk(disks))
        {
            return;
        }

        TempDirResetService.Reset();

        MessageBox.Show(
            Loc.Format("Msg.StartupTempResetDangling", expanded),
            "ManagedDrive",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>
    /// Runs the tray "Reset TEMP Dirs" action: confirms with the user, resets TEMP/TMP to Windows
    /// defaults, and reports the outcome via a balloon tip.
    /// </summary>
    public async Task ResetFromTrayAsync()
    {
        var confirm = new ConfirmDialog(
            Loc.Get("Msg.ResetTempConfirmTitle"),
            Loc.Get("Msg.ResetTempConfirmBody"));

        if (_ownerWindowProvider() is { } owner)
        {
            confirm.Owner = owner;
        }

        if (confirm.ShowDialog() != true)
        {
            return;
        }

        var result = await Task.Run(_restoreTemp);
        _trayIconController.ShowBalloonTip(
            "ManagedDrive",
            result switch
            {
                TempRestoreResult.Restored => Loc.Get("Msg.ResetTempSuccess"),
                TempRestoreResult.NothingToRestore => Loc.Get("Msg.ResetTempNothing"),
                _ => Loc.Get("Msg.ResetTempFailed"),
            },
            result == TempRestoreResult.Failed ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
    }
}
