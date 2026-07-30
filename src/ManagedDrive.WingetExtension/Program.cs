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
// only take the silent-install path when a package id/name/moniker is explicitly given.
var hasPackageSelector = remainingArgs.Any(a => !a.StartsWith('-'));

if (isInstallOrUpgrade && hasPackageSelector && WinFspVolumeDetector.IsCurrentTempOnWinFspVolume())
{
    var useFullSilent = remainingArgs.Contains("--silent") || remainingArgs.Contains("--disable-interactivity");

    if (SilentInstaller.TryInstall(remainingArgs, useFullSilent, out var handledExitCode))
    {
        return handledExitCode;
    }

    // Unsupported installer type: fall back to plain winget.
}

return ProcessForwarder.Run("winget", args);
