namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// One per-user environment variable that points into a RAM disk, for example
/// <c>npm_config_cache</c> pointing at <c>R:\npm-cache</c>. The target is stored relative to the
/// disk so it follows the disk to another mount point.
/// </summary>
public sealed record EnvRedirect
{
    /// <summary>
    /// Gets the name of the environment variable.
    /// </summary>
    public string Variable { get; init; } = string.Empty;

    /// <summary>
    /// Gets the folder, relative to the disk's root, that the variable points at.
    /// </summary>
    public string SubPath { get; init; } = string.Empty;
}
