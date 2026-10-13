namespace ManagedDrive.App.Services;

/// <summary>
/// How the installed WinFsp compares with <see cref="WinFspPrerequisite.RequiredVersion"/>.
/// </summary>
public enum WinFspStatus
{
    /// <summary>The required version is installed.</summary>
    Ok,

    /// <summary>No WinFsp runtime (<c>winfsp-msil.dll</c>) was found.</summary>
    Missing,

    /// <summary>WinFsp is installed, but older than the required version.</summary>
    VersionMismatch,

    /// <summary>WinFsp is installed and newer than the required version; a downgrade is not offered.</summary>
    Newer,
}

/// <summary>
/// Result of <see cref="WinFspPrerequisite.Check"/>.
/// </summary>
/// <param name="Status">How the installed version compares with the required one.</param>
/// <param name="InstalledVersion">The installed version, or <see langword="null"/> when WinFsp is missing.</param>
public readonly record struct WinFspCheckResult(WinFspStatus Status, Version? InstalledVersion);

/// <summary>
/// Checks that the WinFsp runtime ManagedDrive is built and tested against (or a newer one) is installed, and holds
/// the facts about that release (version, download URL, SHA256) in one place. The caller decides what to do
/// (offer an install, warn, shut down) from the result.
/// </summary>
public static class WinFspPrerequisite
{
    /// <summary>
    /// The WinFsp version ManagedDrive requires (2026 Beta5). Keep in step with <c>winfsp.net</c> in
    /// <c>Directory.Packages.props</c> and <c>installer/ManagedDrive.iss</c>.
    /// </summary>
    public static readonly Version RequiredVersion = new(2, 2, 26286);

    /// <summary>
    /// GitHub release tag of <see cref="RequiredVersion"/> (it differs from the file version).
    /// </summary>
    private const string ReleaseTag = "v2.2B5";

    /// <summary>
    /// Display text of <see cref="RequiredVersion"/>.
    /// </summary>
    public static string RequiredVersionText => RequiredVersion.ToString();

    /// <summary>
    /// File name of the required release's installer.
    /// </summary>
    public static readonly string MsiFileName = $"winfsp-{RequiredVersion}.msi";

    /// <summary>
    /// Where the required release's installer is downloaded from.
    /// </summary>
    public static readonly string MsiDownloadUrl =
        $"https://github.com/winfsp/winfsp/releases/download/{ReleaseTag}/{MsiFileName}";

    /// <summary>
    /// SHA256 (hex) of the installer at <see cref="MsiDownloadUrl"/>; the downloaded file is rejected unless it matches.
    /// </summary>
    public const string MsiSha256 = "293FFB9BCC0B625C85079FE85FA77F8FE98170ABA51A02AC9C83D58F6C3B0E2F";

    /// <summary>
    /// The release page, opened when the installer cannot be fetched or run automatically.
    /// </summary>
    public static readonly string ReleasePageUrl = $"https://github.com/winfsp/winfsp/releases/tag/{ReleaseTag}";

    /// <summary>
    /// The value remembered when the user declines to update: it names both the installed and the required
    /// version, so the question is asked again when either changes.
    /// </summary>
    public static string SkipKey(Version installed) => $"{installed}>{RequiredVersion}";

    /// <summary>
    /// Whether the user should be asked about <paramref name="check"/>: never for an acceptable install, always
    /// for a missing one, and for an older one unless the same update was already declined.
    /// </summary>
    /// <param name="check">The result of <see cref="Check"/>.</param>
    /// <param name="skippedKey">The remembered <see cref="SkipKey"/>, or <see langword="null"/>.</param>
    public static bool ShouldPrompt(WinFspCheckResult check, string? skippedKey) => check.Status switch
    {
        WinFspStatus.Missing => true,
        WinFspStatus.VersionMismatch => skippedKey != SkipKey(check.InstalledVersion!),
        _ => false,
    };

    /// <summary>
    /// Reads the installed WinFsp runtime from disk and compares it with <see cref="RequiredVersion"/>.
    /// </summary>
    public static WinFspCheckResult Check()
    {
        var installed = GetInstalledVersion();
        return new(Evaluate(installed), installed);
    }

    /// <summary>
    /// Compares an installed version with <see cref="RequiredVersion"/>.
    /// </summary>
    /// <param name="installed">The installed version, or <see langword="null"/> when WinFsp is not installed.</param>
    public static WinFspStatus Evaluate(Version? installed)
    {
        if (installed is null)
        {
            return WinFspStatus.Missing;
        }

        var installedCore = new Version(installed.Major, installed.Minor, installed.Build);
        var comparison = installedCore.CompareTo(RequiredVersion);
        return comparison == 0 ? WinFspStatus.Ok : comparison < 0 ? WinFspStatus.VersionMismatch : WinFspStatus.Newer;
    }

    /// <summary>
    /// Returns the file version of the system-installed <c>winfsp-msil.dll</c> (found through the
    /// <c>InstallDir</c> registry value, normally <c>C:\Program Files (x86)\WinFsp\bin\</c>), or
    /// <see langword="null"/> when it is not there.
    /// </summary>
    private static Version? GetInstalledVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp")
                        ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");

        if (key?.GetValue("InstallDir") is not string installDir)
        {
            return null;
        }

        var dllPath = Path.Combine(installDir, "bin", "winfsp-msil.dll");
        if (!File.Exists(dllPath))
        {
            return null;
        }

        var info = FileVersionInfo.GetVersionInfo(dllPath);
        return new(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }
}
