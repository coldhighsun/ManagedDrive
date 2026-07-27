namespace ManagedDrive.WingetExtension;

// Routes an `install`/`upgrade` request through `winget download` + a manual launch of the
// downloaded installer (msiexec for MSI/WiX, the installer exe itself otherwise) whenever the
// package is MSI- or exe-based, so the installer file lives on a real (non-WinFsp) volume by the
// time it runs — sidestepping both msiexec's Mount-Manager source-volume check and whatever
// cross-session hiccup causes plain `winget install` to return generic exit code 1 for exe
// installers while TEMP points at a WinFsp volume. Installer types this can't confidently handle
// (msix, appx, zip, portable, ...) are left for the caller to hand off to plain `winget install`.
internal static class SilentInstaller
{
    // Returns true if this call fully handled the request (exitCode is authoritative).
    // Returns false if the package's installer type isn't one this class knows how to run
    // directly, and the caller should fall back to a plain `winget install`/`winget upgrade`.
    public static bool TryInstall(IReadOnlyList<string> packageSelectorArgs, bool useFullSilent, out int exitCode)
    {
        var downloadDirectory = CreateRealTempDirectory();
        try
        {
            Console.WriteLine($"wingetx: downloading {string.Join(' ', packageSelectorArgs)} installer...");

            var downloadArgs = new List<string>
            {
                "download",
                "-d", downloadDirectory,
                "--accept-package-agreements",
                "--accept-source-agreements",
            };
            downloadArgs.AddRange(packageSelectorArgs);

            var downloadExitCode = ProcessForwarder.Run("winget", downloadArgs);
            if (downloadExitCode != 0)
            {
                exitCode = downloadExitCode;
                return true;
            }

            var manifestPath = Directory.GetFiles(downloadDirectory, "*.yaml")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (manifestPath is null)
            {
                Console.Error.WriteLine("wingetx: winget download reported success but produced no manifest file; falling back to `winget install`.");
                exitCode = 0;
                return false;
            }

            var installerInfo = WingetManifestReader.ReadInstallerInfo(manifestPath);
            var installerPath = Directory.GetFiles(downloadDirectory)
                .Where(path => !path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"winget download produced no installer file in '{downloadDirectory}'.");

            var switches = useFullSilent ? installerInfo.SilentSwitches : installerInfo.SilentWithProgressSwitches;

            if (WingetManifestReader.IsMsiBased(installerInfo.InstallerType))
            {
                Console.WriteLine("wingetx: running installer (this may take a moment)...");
                var msiexecArgs = new List<string> { "/i", installerPath };
                msiexecArgs.AddRange(SplitSwitches(switches));
                exitCode = ProcessForwarder.Run("msiexec", msiexecArgs);
                return true;
            }

            if (WingetManifestReader.IsExeBased(installerInfo.InstallerType))
            {
                Console.WriteLine("wingetx: running installer (this may take a moment)...");
                exitCode = ProcessForwarder.Run(installerPath, SplitSwitches(switches).ToList());
                return true;
            }

            exitCode = 0;
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            Console.Error.WriteLine($"wingetx: {ex.Message}");
            exitCode = 1;
            return true;
        }
        finally
        {
            TryDeleteDirectory(downloadDirectory);
        }
    }

    // %LOCALAPPDATA%\Temp is the OS default location %TEMP%/%TMP% normally point to before any
    // WinFsp redirection — it's on the real system volume (satisfying the same Mount-Manager /
    // cross-session constraint %WINDIR%\Temp would), but stays inside the invoking user's own
    // profile instead of a machine-wide directory shared by every user/service on the box.
    private static string CreateRealTempDirectory()
    {
        var userTempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "wingetx");
        var directory = Path.Combine(userTempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; leaving a stray download behind is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Minimal whitespace tokenizer that keeps double-quoted segments intact
    // (manifest switches occasionally quote paths, e.g. `/log "<LOGPATH>"`).
    private static IEnumerable<string> SplitSwitches(string switches)
    {
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in switches)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
