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
    /// Gets or sets a value indicating whether the Windows Explorer right-click context menu
    /// entry for importing archive files as RAM disks is registered.
    /// </summary>
    public bool ContextMenuEnabled
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets a value indicating whether ManagedDrive automatically checks GitHub Releases
    /// for a newer version at startup, at most once per day. Manual checks from the About dialog
    /// are not affected by this setting. Defaults to <c>true</c>.
    /// </summary>
    public bool AutoCheckForUpdates { get; init; } = true;

    /// <summary>
    /// Gets or sets the timestamp of the last successful update check, used to throttle
    /// automatic checks to at most once per day. <c>null</c> if never checked.
    /// </summary>
    public DateTimeOffset? LastUpdateCheckUtc
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets the version the user chose to skip notifications for via the "Skip This
    /// Version" option, or <c>null</c> if no version is currently skipped.
    /// </summary>
    public string? SkippedVersion
    {
        get; init;
    }
}