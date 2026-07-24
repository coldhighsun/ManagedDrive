namespace ManagedDrive.App.Models;

/// <summary>
/// Describes a newer release found via <see cref="UpdateCheckService"/>.
/// </summary>
public sealed record UpdateInfo(string Version, Uri ReleaseUrl);
