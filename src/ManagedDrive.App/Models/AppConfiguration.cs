namespace ManagedDrive.App.Models;

/// <summary>
/// Top-level application configuration persisted to
/// <c>%APPDATA%\ManagedDrive\settings.json</c>.
/// Contains both global application settings and the list of saved disk profiles.
/// </summary>
public sealed record AppConfiguration
{
    /// <summary>
    /// Gets or sets the list of RAM disk profiles to restore on startup.
    /// </summary>
    public List<DiskProfile> Disks { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether ManagedDrive is registered to run
    /// automatically when Windows starts.
    /// </summary>
    public bool RunAtStartup
    {
        get; init;
    }
}