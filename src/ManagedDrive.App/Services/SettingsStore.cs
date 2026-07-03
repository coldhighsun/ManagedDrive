using System.Text.Json;

namespace ManagedDrive.App.Services;

/// <summary>
/// Persists and restores the unified <see cref="AppConfiguration"/> as a JSON file stored in
/// the user's <c>%APPDATA%\ManagedDrive</c> folder.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    /// <summary>
    /// Initializes the store, creating the application data directory if it does not exist.
    /// </summary>
    public SettingsStore()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ManagedDrive");

        Directory.CreateDirectory(appDataDir);
        _settingsPath = Path.Combine(appDataDir, "settings.json");
    }

    /// <summary>
    /// Loads the application configuration from disk.
    /// Returns a default <see cref="AppConfiguration"/> when the file does not exist
    /// or cannot be parsed.
    /// </summary>
    /// <returns>
    /// The deserialized <see cref="AppConfiguration"/>, or a fresh default instance on failure.
    /// </returns>
    public AppConfiguration Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppConfiguration();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions)
                ?? new AppConfiguration();
        }
        catch
        {
            return new AppConfiguration();
        }
    }

    /// <summary>
    /// Writes the supplied configuration to the settings file, overwriting any previous data.
    /// </summary>
    /// <param name="config">The configuration to persist.</param>
    public void Save(AppConfiguration config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}