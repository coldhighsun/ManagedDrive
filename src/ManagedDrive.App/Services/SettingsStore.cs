using System.Text.Json;

namespace ManagedDrive.App.Services;

/// <summary>
/// Persists and restores the unified <see cref="AppConfiguration"/> as a JSON file stored in
/// the user's <c>%APPDATA%\ManagedDrive</c> folder.
/// </summary>
public sealed class SettingsStore
{
    /// <summary>
    /// Logger for load/save failures, bridged through <see cref="AppLog"/> since this store is
    /// created before the rest of the app's services are wired up.
    /// </summary>
    private static readonly ILogger Logger = AppLog.CreateLogger<SettingsStore>();

    /// <summary>
    /// Serializer options shared by <see cref="Load"/> and <see cref="Save"/>.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Full path of the settings file.
    /// </summary>
    private readonly string _settingsPath;

    /// <summary>
    /// Serializes <see cref="Load"/>/<see cref="Save"/> within the process, so two concurrent
    /// saves (e.g. the UI thread and an update check's continuation) never write the same temp
    /// file at once or read a half-replaced settings file.
    /// </summary>
    private readonly Lock _ioLock = new();

    /// <summary>
    /// Initializes the store, creating the application data directory if it does not exist.
    /// </summary>
    public SettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ManagedDrive",
            "settings.json"))
    {
    }

    /// <summary>
    /// Initializes the store against an explicit settings file path, creating its directory if it
    /// does not exist. Test hook; the app uses the parameterless constructor.
    /// </summary>
    /// <param name="settingsPath">Full path of the settings file.</param>
    internal SettingsStore(string settingsPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        _settingsPath = settingsPath;
    }

    /// <summary>
    /// Loads the application configuration from disk.
    /// Returns a default <see cref="AppConfiguration"/> when the file does not exist
    /// or cannot be parsed. An unparseable file is first copied aside (see
    /// <see cref="PreserveUnreadableFile"/>) so the next <see cref="Save"/> — which would
    /// otherwise overwrite it with defaults — can't destroy the user's only copy.
    /// </summary>
    /// <returns>
    /// The deserialized <see cref="AppConfiguration"/>, or a fresh default instance on failure.
    /// </returns>
    public AppConfiguration Load()
    {
        lock (_ioLock)
        {
            if (!File.Exists(_settingsPath))
            {
                return new();
            }

            try
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions)
                    ?? new AppConfiguration();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Logger.LogError(ex, "Failed to read settings from '{SettingsPath}'; falling back to defaults", _settingsPath);
                PreserveUnreadableFile();
                return new();
            }
        }
    }

    /// <summary>
    /// Writes the supplied configuration to the settings file, overwriting any previous data.
    /// Writes to a temporary sibling file first and then swaps it into place, so an interrupted
    /// save (crash, power loss) leaves either the old or the new file intact — never a truncated
    /// one that the next <see cref="Load"/> would discard.
    /// </summary>
    /// <param name="config">The configuration to persist.</param>
    public void Save(AppConfiguration config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tempPath = _settingsPath + ".tmp";

        lock (_ioLock)
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _settingsPath, overwrite: true);
        }
    }

    /// <summary>
    /// Atomically reads the current configuration, applies <paramref name="change"/> to it and
    /// saves the result, so a concurrent writer (e.g. an update check's continuation recording its
    /// check time) can't slip a save in between the read and the write and have it overwritten
    /// with the stale value read here.
    /// </summary>
    /// <param name="change">
    /// Produces the configuration to save from the one currently on disk. Runs under the store's
    /// lock, so it must not call back into this store from another thread.
    /// </param>
    public void Update(Func<AppConfiguration, AppConfiguration> change)
    {
        // Lock is reentrant, so Load/Save taking it again on this thread is fine.
        lock (_ioLock)
        {
            Save(change(Load()));
        }
    }

    /// <summary>
    /// Copies an unreadable settings file to a timestamped <c>.corrupt</c> sibling. Best-effort:
    /// failing to make the copy is logged and otherwise ignored, since startup must proceed.
    /// </summary>
    private void PreserveUnreadableFile()
    {
        var backupPath = $"{_settingsPath}.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Copy(_settingsPath, backupPath, overwrite: true);
            Logger.LogWarning("Copied unreadable settings file to '{BackupPath}'", backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Failed to copy unreadable settings file to '{BackupPath}'", backupPath);
        }
    }
}
