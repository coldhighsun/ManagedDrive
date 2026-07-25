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

    /// <param name="settings">Used to persist <see cref="AppConfiguration.TempDirCompatWarningShown"/>.</param>
    /// <param name="trayIconController">Used to show the reset-result balloon tip.</param>
    /// <param name="ownerWindowProvider">Supplies the confirm dialog's owner window, or <c>null</c> if none is loaded.</param>
    public TempDirCompatChecker(SettingsStore settings, TrayIconController trayIconController, Func<Window?> ownerWindowProvider)
    {
        _settings = settings;
        _trayIconController = trayIconController;
        _ownerWindowProvider = ownerWindowProvider;
    }

    /// <summary>
    /// Returns <c>true</c> when the user-level TEMP variable currently points into any of
    /// <paramref name="disks"/>.
    /// </summary>
    public static bool IsTempOnAnyDisk(IEnumerable<DiskViewModel> disks)
    {
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        if (string.IsNullOrEmpty(userTemp))
        {
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(userTemp);
        return disks.Any(d => expanded.StartsWith(d.MountPoint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Runs the one-time startup check: if TEMP points at a drive letter matching a saved disk
    /// profile that isn't set to auto-mount, resets TEMP and warns; if it matches an auto-mount
    /// profile, warns once (since elevated processes still can't reach WinFsp drives) and records
    /// that the warning was shown.
    /// </summary>
    public void CheckOnStartup(AppConfiguration config)
    {
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        if (string.IsNullOrEmpty(userTemp))
        {
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(userTemp);
        if (expanded.Length < 2 || !char.IsLetter(expanded[0]) || expanded[1] != ':')
        {
            return;
        }

        var mountPoint = char.ToUpperInvariant(expanded[0]) + ":";
        var matchingProfile = config.Disks.FirstOrDefault(d =>
            string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));

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

                _settings.Save(config with
                {
                    TempDirCompatWarningShown = true
                });
            }
        }
    }

    /// <summary>
    /// Runs a post-auto-mount safety check: if TEMP still points at a drive letter matching a
    /// saved disk profile, but no <em>currently live</em> disk actually occupies that mount point,
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
        if (expanded.Length < 2 || !char.IsLetter(expanded[0]) || expanded[1] != ':')
        {
            return;
        }

        var mountPoint = char.ToUpperInvariant(expanded[0]) + ":";
        var isKnownProfile = config.Disks.Any(d =>
            string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
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

        var success = await Task.Run(TempDirResetService.Reset);
        _trayIconController.ShowBalloonTip(
            "ManagedDrive",
            success ? Loc.Get("Msg.ResetTempSuccess") : Loc.Get("Msg.ResetTempFailed"),
            success ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
    }
}