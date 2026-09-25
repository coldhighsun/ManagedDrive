using ManagedDrive.WingetExtension;
using WinFspVolumeDetector = ManagedDrive.WingetExtension.WinFspVolumeDetector;

// Transparent winget wrapper: routes MSI- and exe-based `install`/`upgrade` through winget
// download + a manual launch of the downloaded installer when %TEMP% sits on a WinFsp volume, to
// sidestep both msiexec's Mount-Manager source-volume check and the cross-session hiccup that
// makes plain `winget install` fail exe installers with generic exit code 1 in the same situation
// (see: E:\repos\ManagedDrive CLAUDE.md "MSI installers" limitation). Installer types this can't
// confidently handle (msix, appx, zip, portable, ...) are forwarded to `winget.exe` unchanged.

var subcommand = args.Length > 0 ? args[0] : null;
var remainingArgs = args.Length > 0 ? args[1..] : [];

var isInstallOrUpgrade = subcommand is "install" or "upgrade";

// `winget download` (used by SilentInstaller) requires exactly one target package and has
// no "all outdated packages" mode, unlike `winget upgrade`/`winget install` with no id — so
// only take the silent-install path when exactly one package is explicitly given, and only with
// options a direct installer launch can honor.
if (isInstallOrUpgrade &&
    WingetInstallArguments.TryParse(remainingArgs, out var parsed) &&
    WinFspVolumeDetector.IsCurrentTempOnWinFspVolume())
{
    if (SilentInstaller.TryInstall(parsed.DownloadArgs, parsed.UseFullSilent, out var handledExitCode))
    {
        if (parsed.WaitForKeyPress)
        {
            KeyPressPrompt.Wait();
        }

        return handledExitCode;
    }

    // Unsupported installer type: fall back to plain winget.
}

return ProcessForwarder.Run("winget", args);
