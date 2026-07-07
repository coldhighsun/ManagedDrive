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
    /// Theme choice for the UI: "light", "dark", or <c>null</c> to follow the Windows system theme.
    /// </summary>
    public string? Theme
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

    /// <summary>
    /// Gets or sets the usage percentage (0-100) at which a disk is flagged as high-usage and
    /// a tray warning is shown. Defaults to 90%.
    /// </summary>
    public double HighUsageWarnPercent { get; init; } = 90.0;

    /// <summary>
    /// Gets or sets the usage percentage (0-100) below which a disk's high-usage flag is
    /// cleared, re-arming the warning. Must be lower than <see cref="HighUsageWarnPercent"/> to
    /// provide hysteresis. Defaults to 85%.
    /// </summary>
    public double HighUsageResetPercent { get; init; } = 85.0;
}