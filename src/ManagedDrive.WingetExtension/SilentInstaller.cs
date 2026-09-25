using YamlDotNet.Core;

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
    /// <summary>
    /// Downloads the package with <c>winget download</c> to a real (non-WinFsp) directory and
    /// runs its installer directly.
    /// </summary>
    /// <param name="packageSelectorArgs">
    /// The arguments to pass on to <c>winget download</c>; must only hold arguments it accepts
    /// (see <see cref="WingetInstallArguments"/>).
    /// </param>
    /// <param name="useFullSilent">
    /// Whether to use the installer's fully silent switches rather than silent-with-progress.
    /// </param>
    /// <param name="exitCode">The exit code to return, when this returns <c>true</c>.</param>
    /// <returns>
    /// <c>true</c> if this call fully handled the request (<paramref name="exitCode"/> is
    /// authoritative); <c>false</c> if the package's installer type isn't one this class knows
    /// how to run directly, and the caller should fall back to a plain
    /// <c>winget install</c>/<c>winget upgrade</c>.
    /// </returns>
    public static bool TryInstall(IReadOnlyList<string> packageSelectorArgs, bool useFullSilent, out int exitCode)
    {
        string? downloadDirectory = null;
        try
        {
            downloadDirectory = CreateRealTempDirectory();

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
        catch (Exception ex) when (ShouldFallBack(ex))
        {
            Console.Error.WriteLine($"wingetx: {ex.Message} Falling back to `winget install`.");
            exitCode = 0;
            return false;
        }
        catch (Exception ex) when (IsInstallFailure(ex))
        {
            Console.Error.WriteLine($"wingetx: {ex.Message}");
            exitCode = 1;
            return true;
        }
        finally
        {
            if (downloadDirectory is not null)
            {
                TryDeleteDirectory(downloadDirectory);
            }
        }
    }

    /// <summary>
    /// Returns whether an exception thrown while handling a request means this class can't run
    /// the package's installer, so the caller should fall back to a plain <c>winget install</c>:
    /// the manifest names an installer type with no known default switches and doesn't specify
    /// its own (e.g. a plain <c>exe</c>, or a <c>zip</c>/<c>msix</c> this class never runs).
    /// </summary>
    /// <param name="exception">The exception thrown.</param>
    /// <returns><c>true</c> if the caller should fall back to a plain <c>winget install</c>.</returns>
    internal static bool ShouldFallBack(Exception exception) => exception is NotSupportedException;

    /// <summary>
    /// Returns whether an exception thrown while handling a request is an expected failure to
    /// report as exit code 1 (an unusable or unreadable manifest or download directory), rather
    /// than a bug to let crash the process.
    /// </summary>
    /// <param name="exception">The exception thrown.</param>
    /// <returns><c>true</c> if the failure should be reported and the request treated as handled.</returns>
    internal static bool IsInstallFailure(Exception exception) =>
        exception is InvalidOperationException or YamlException or IOException or UnauthorizedAccessException;

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
