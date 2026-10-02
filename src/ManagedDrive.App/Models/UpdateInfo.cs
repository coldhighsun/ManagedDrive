using GitHubReleaseUpdater;

namespace ManagedDrive.App.Models;

/// <summary>
/// Describes a newer release found via <see cref="UpdateCheckService"/>.
/// </summary>
/// <param name="Version">Display version of the new release.</param>
/// <param name="ReleaseUrl">The release page on GitHub, used as a manual-download fallback.</param>
/// <param name="ReleaseNotes">The release notes (Markdown) published with the release, if any.</param>
/// <param name="InstallerSize">Size in bytes of the installer asset, or <see langword="null"/> when none matched.</param>
/// <param name="Check">The library result this update came from, handed back to GitHubReleaseUpdater to download it.</param>
public sealed record UpdateInfo(string Version, Uri ReleaseUrl, string? ReleaseNotes, long? InstallerSize, UpdateCheckResult Check)
{
    /// <summary>
    /// Whether the release has an installer asset the app can download and run itself. When it does not,
    /// the user can only be pointed at <see cref="ReleaseUrl"/>.
    /// </summary>
    public bool CanInstall => Check.Update?.SelectedAsset is not null;
}
