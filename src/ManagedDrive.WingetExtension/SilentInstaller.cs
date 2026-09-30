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
    /// <param name="isUpgrade">
    /// Whether the request is <c>winget upgrade</c> (rather than <c>install</c>), which decides
    /// when plain winget must handle it instead.
    /// </param>
    /// <param name="exitCode">The exit code to return, when this returns <c>true</c>.</param>
    /// <returns>
    /// <c>true</c> if this call fully handled the request (<paramref name="exitCode"/> is
    /// authoritative); <c>false</c> if the request is one this class can't or shouldn't run
    /// itself — an installer type it doesn't know how to run, a failed download, or a package
    /// whose installed state plain winget must judge (already installed, or nothing to upgrade)
    /// — and the caller should fall back to a plain <c>winget install</c>/<c>winget upgrade</c>.
    /// </returns>
    public static bool TryInstall(IReadOnlyList<string> packageSelectorArgs, bool useFullSilent, bool isUpgrade, out int exitCode)
    {
        string? downloadDirectory = null;
        try
        {
            // An explicit --id makes the installed-state check possible before anything is
            // downloaded, which matters for `upgrade` of a package that is already current.
            if (ExplicitPackageId(packageSelectorArgs) is { } requestedId && ShouldDeferToWinget(isUpgrade, requestedId, IsExactRequest(packageSelectorArgs)))
            {
                exitCode = 0;
                return false;
            }

            downloadDirectory = CreateRealTempDirectory();

            Console.WriteLine($"wingetx: downloading {string.Join(' ', packageSelectorArgs)} installer...");

            var downloadExitCode = ProcessForwarder.Run("winget", BuildDownloadArguments(downloadDirectory, packageSelectorArgs));
            if (downloadExitCode != 0)
            {
                // Not reported as handled: plain winget can install packages winget download
                // can't fetch (e.g. msstore sources) and words the failures it can't itself
                // resolve better than a bare exit code from an intermediate step.
                Console.Error.WriteLine($"wingetx: winget download failed (exit code {downloadExitCode}). Falling back to `winget install`.");
                exitCode = 0;
                return false;
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

            var installerPath = Directory.GetFiles(downloadDirectory)
                .Where(path => !path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"winget download produced no installer file in '{downloadDirectory}'.");

            var installerInfo = WingetManifestReader.ReadInstallerInfo(
                manifestPath, Path.GetFileName(installerPath), ArchitectureOf(packageSelectorArgs));

            if (ExplicitPackageId(packageSelectorArgs) is null && installerInfo.PackageIdentifier is { } id &&
                ShouldDeferToWinget(isUpgrade, id, exact: true))
            {
                exitCode = 0;
                return false;
            }

            var switches = useFullSilent ? installerInfo.SilentSwitches : installerInfo.SilentWithProgressSwitches;

            if (WingetManifestReader.IsMsiBased(installerInfo.InstallerType))
            {
                Console.WriteLine("wingetx: running installer (this may take a moment)...");
                var msiexecArgs = new List<string> { "/i", installerPath };
                msiexecArgs.AddRange(SplitSwitches(switches));
                exitCode = MapInstallerExitCode(ProcessForwarder.Run("msiexec", msiexecArgs));
                return true;
            }

            if (WingetManifestReader.IsExeBased(installerInfo.InstallerType))
            {
                Console.WriteLine("wingetx: running installer (this may take a moment)...");
                exitCode = MapInstallerExitCode(ProcessForwarder.Run(installerPath, SplitSwitches(switches).ToList()));
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

    /// <summary>
    /// The agreement flags a non-interactive <c>winget download</c> needs.
    /// </summary>
    private static readonly string[] AgreementFlags = ["--accept-package-agreements", "--accept-source-agreements"];

    /// <summary>
    /// Builds the arguments for <c>winget download</c>: the request's own selector arguments plus a
    /// target directory and the agreement flags a non-interactive download needs. A flag the user
    /// already passed is not repeated, since winget rejects a duplicated argument.
    /// </summary>
    /// <param name="downloadDirectory">The directory to download into.</param>
    /// <param name="packageSelectorArgs">The request's own arguments.</param>
    /// <returns>The full argument list, starting with <c>download</c>.</returns>
    internal static List<string> BuildDownloadArguments(string downloadDirectory, IReadOnlyList<string> packageSelectorArgs)
    {
        var downloadArgs = new List<string> { "download", "-d", downloadDirectory };

        foreach (var agreementFlag in AgreementFlags)
        {
            if (!packageSelectorArgs.Contains(agreementFlag, StringComparer.Ordinal))
            {
                downloadArgs.Add(agreementFlag);
            }
        }

        downloadArgs.AddRange(packageSelectorArgs);
        return downloadArgs;
    }

    /// <summary>
    /// Maps the exit codes that mean the installer succeeded but wants a restart (MSI's
    /// ERROR_SUCCESS_REBOOT_REQUIRED, 3010, and ERROR_SUCCESS_REBOOT_INITIATED, 1641) to success,
    /// since winget reports these installs as successful.
    /// </summary>
    /// <param name="installerExitCode">The installer's exit code.</param>
    /// <returns>0 for a restart-required code; otherwise <paramref name="installerExitCode"/>.</returns>
    internal static int MapInstallerExitCode(int installerExitCode)
    {
        if (installerExitCode is 3010 or 1641)
        {
            Console.WriteLine("wingetx: the installer succeeded and requires a restart to finish.");
            return 0;
        }

        return installerExitCode;
    }

    /// <summary>
    /// Whether the request asks for an exact match (<c>-e</c>/<c>--exact</c>).
    /// </summary>
    /// <param name="args">The request's arguments.</param>
    /// <returns><c>true</c> if an exact match was requested.</returns>
    internal static bool IsExactRequest(IReadOnlyList<string> args) =>
        args.Contains("-e", StringComparer.Ordinal) || args.Contains("--exact", StringComparer.Ordinal);

    /// <summary>
    /// Gets the value of the request's <c>--id</c> option.
    /// </summary>
    /// <param name="args">The request's arguments.</param>
    /// <returns>The package id, or <c>null</c> if the request has none.</returns>
    internal static string? ExplicitPackageId(IReadOnlyList<string> args) => OptionValue(args, "--id");

    /// <summary>
    /// Gets the value of the request's <c>-a</c>/<c>--architecture</c> option.
    /// </summary>
    /// <param name="args">The request's arguments.</param>
    /// <returns>The architecture, or <c>null</c> if the request has none.</returns>
    internal static string? ArchitectureOf(IReadOnlyList<string> args) =>
        OptionValue(args, "--architecture") ?? OptionValue(args, "-a");

    /// <summary>
    /// Gets the value of an option given either as <c>name value</c> or <c>name=value</c>.
    /// </summary>
    /// <param name="args">The request's arguments.</param>
    /// <param name="name">The option name, e.g. <c>--id</c>.</param>
    /// <returns>The value, or <c>null</c> if the option is absent or has no value.</returns>
    private static string? OptionValue(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == name)
            {
                return i + 1 < args.Count ? args[i + 1] : null;
            }

            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }

    /// <summary>
    /// Interprets the output of the <c>winget list</c> probe: the package id appears in the table
    /// exactly when the package is installed (or, with <c>--upgrade-available</c>, has an upgrade).
    /// </summary>
    /// <param name="isUpgrade">Whether the request is <c>upgrade</c> rather than <c>install</c>.</param>
    /// <param name="packageId">The package's id.</param>
    /// <param name="probeOutput">What the probe printed.</param>
    /// <returns>
    /// <c>true</c> when plain winget should handle the request: an <c>install</c> of a package that
    /// is already installed, or an <c>upgrade</c> with nothing to upgrade.
    /// </returns>
    internal static bool ProbeOutputMeansDefer(bool isUpgrade, string packageId, string probeOutput, bool exact = true)
    {
        // For an exact request the id must appear as a whole token, so "Foo" doesn't count as
        // installed just because "Foo.Bar" is. A partial id (non-exact) can only ever match as a
        // substring of the listed full id.
        var listed = exact
            ? System.Text.RegularExpressions.Regex.IsMatch(
                probeOutput,
                $@"(?<![\w.\-]){System.Text.RegularExpressions.Regex.Escape(packageId)}(?![\w.\-])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            : probeOutput.Contains(packageId, StringComparison.OrdinalIgnoreCase);
        return isUpgrade ? !listed : listed;
    }

    /// <summary>
    /// Decides whether plain winget must handle the request because it depends on what is
    /// installed: <c>install</c> of an already installed package (winget upgrades or reports it,
    /// where running the installer again would reinstall), and <c>upgrade</c> of a package that
    /// isn't installed or has no newer version (winget reports that, where running the downloaded
    /// installer would install the package or reinstall the same version). winget's output is
    /// localized, so the answer is read from whether the package ID (which no message echoes)
    /// appears in the table <c>winget list</c> prints.
    /// </summary>
    /// <param name="isUpgrade">Whether the request is <c>upgrade</c> rather than <c>install</c>.</param>
    /// <param name="packageId">The package's id.</param>
    /// <param name="exact">
    /// Whether to match the id exactly. Only true when the user asked for an exact match or the id
    /// comes from the downloaded manifest; otherwise a partial id would never match.
    /// </param>
    /// <returns>
    /// <c>true</c> if plain winget should handle it; <c>false</c> if wingetx should proceed, which
    /// is also the answer when the probe can't run.
    /// </returns>
    private static bool ShouldDeferToWinget(bool isUpgrade, string packageId, bool exact)
    {
        var probeArgs = new List<string> { "list" };
        if (isUpgrade)
        {
            probeArgs.Add("--upgrade-available");
        }

        if (exact)
        {
            probeArgs.Add("--exact");
        }

        probeArgs.AddRange(["--id", packageId, "--accept-source-agreements", "--disable-interactivity"]);

        if (ProcessForwarder.RunCapture("winget", probeArgs, TimeSpan.FromSeconds(60), out var output) is null)
        {
            return false;
        }

        var defer = ProbeOutputMeansDefer(isUpgrade, packageId, output, exact);
        if (defer)
        {
            Console.WriteLine(isUpgrade
                ? "wingetx: no upgrade to run for this package; handing over to winget."
                : "wingetx: the package is already installed; handing over to winget.");
        }

        return defer;
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
