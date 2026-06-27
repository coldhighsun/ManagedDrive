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

    /// <summary>
    /// BCP-47 language tag for the UI language, e.g. "en-US" or "zh-CN".
    /// Null means no explicit choice has been made — the app will auto-detect from the system locale.
    /// </summary>
    public string? Language
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the main window should be hidden on startup,
    /// showing only the system tray icon. Defaults to <c>false</c>.
    /// </summary>
    public bool StartMinimized
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the RAM-disk-as-temp compatibility warning has
    /// already been shown to the user. When <c>true</c> the warning is suppressed on subsequent
    /// "Set as Temp" actions.
    /// </summary>
    public bool TempDirCompatWarningShown
    {
        get; init;
    }
}