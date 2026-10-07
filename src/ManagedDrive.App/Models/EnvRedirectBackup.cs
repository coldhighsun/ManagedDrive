using Microsoft.Win32;

namespace ManagedDrive.App.Models;

/// <summary>
/// An environment variable that is currently pointed into a RAM disk, together with what it held
/// before. Stored in <see cref="Services.EnvRedirectBackupStore"/> while the redirection is
/// active.
/// </summary>
public sealed record EnvRedirectBackup
{
    /// <summary>
    /// Gets or sets the name of the variable.
    /// </summary>
    public string Variable { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the mount point of the disk the variable points into.
    /// </summary>
    public string MountPoint { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the value written to the registry. Restoring only happens while the variable
    /// still holds it, so a value the user changed in the meantime is left alone.
    /// </summary>
    public string AppliedValue { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the unexpanded text the variable held before, or <c>null</c> if it was not set.
    /// </summary>
    public string? OriginalText
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets the registry value kind of <see cref="OriginalText"/>.
    /// </summary>
    public RegistryValueKind OriginalKind { get; init; } = RegistryValueKind.String;
}
