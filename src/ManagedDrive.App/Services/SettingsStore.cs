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
    /// Gets the folder that holds the settings file, where the app keeps its other small state files.
    /// </summary>
    internal string DirectoryPath => Path.GetDirectoryName(_settingsPath)!;

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
        // Only the first Load (the one that seeds the app's in-memory state at startup) can leave
        // that state built from fallback defaults; a later failed Load just returns defaults to
        // its caller, which doesn't save them.
        var isStartupLoad = Interlocked.Exchange(ref _startupLoadDone, 1) == 0;

        for (var attempt = 0; ; attempt++)
        {
            var config = Read(out var ioFailed, out var retryable);

            // A brief lock (antivirus, backup tool) must not surface as empty defaults: the
            // caller's next save would then replace the user's real settings with them.
            if (!ioFailed)
            {
                return config;
            }

            if (!retryable || attempt >= UpdateReadRetries)
            {
                // The defaults returned here stand in for a file that exists but is unreadable.
                // Update must not save state derived from them over that file.
                if (isStartupLoad)
                {
                    _loadFellBackAfterIoFailure = true;
                }

                return config;
            }

            Thread.Sleep(UpdateReadRetryDelay);
        }
    }

    /// <summary>
    /// Set when the first <see cref="Load"/> gave up on an I/O or access error and returned
    /// defaults instead of the file's contents; stays set for the rest of the session.
    /// </summary>
    private volatile bool _loadFellBackAfterIoFailure;

    /// <summary>
    /// Non-zero once <see cref="Load"/> has been called, so only the first call counts as the
    /// startup load.
    /// </summary>
    private int _startupLoadDone;

    /// <summary>
    /// How many times <see cref="Update"/> re-reads the settings file after an I/O failure.
    /// </summary>
    private const int UpdateReadRetries = 3;

    /// <summary>
    /// Pause between the re-reads of <see cref="Update"/> after an I/O failure.
    /// </summary>
    private static readonly TimeSpan UpdateReadRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Implements <see cref="Load"/>, additionally reporting whether the defaults it returned
    /// stand in for a file that exists but could not be read due to an I/O or access error.
    /// </summary>
    /// <param name="ioFailed">
    /// <c>true</c> when the file exists but reading it failed with an I/O or access error (for
    /// example a transient lock held by antivirus or a backup tool), as opposed to it being
    /// missing or containing invalid data.
    /// </param>
    /// <param name="retryable">
    /// <c>true</c> when <paramref name="ioFailed"/> is an I/O error that may be a brief lock, as
    /// opposed to an access-denied, which is normally permanent.
    /// </param>
    /// <returns>
    /// The deserialized <see cref="AppConfiguration"/>, or a fresh default instance on failure.
    /// </returns>
    private AppConfiguration Read(out bool ioFailed, out bool retryable)
    {
        ioFailed = false;
        retryable = false;

        lock (_ioLock)
        {
            if (!File.Exists(_settingsPath))
            {
                return new();
            }

            try
            {
                var json = File.ReadAllText(_settingsPath);
                var config = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions);
                return config is null ? new() : Normalize(config);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                ioFailed = ex is IOException or UnauthorizedAccessException;
                retryable = ex is IOException;
                Logger.LogError(ex, "Failed to read settings from '{SettingsPath}'; falling back to defaults", _settingsPath);

                // A transient lock leaves the file intact, so there is nothing to preserve.
                if (!ioFailed)
                {
                    PreserveUnreadableFile();
                }

                return new();
            }
        }
    }

    /// <summary>
    /// Repairs values that deserialize without error but would crash later consumers: a
    /// <c>null</c> disk list, <c>null</c> list entries, and profiles without a mount point.
    /// </summary>
    /// <param name="config">The freshly deserialized configuration.</param>
    /// <returns>The same configuration with a usable <see cref="AppConfiguration.Disks"/> list.</returns>
    private AppConfiguration Normalize(AppConfiguration config)
    {
        // JSON null bypasses the non-nullable annotations, so the list or its entries can be null.
        var disks = (IEnumerable<DiskProfile?>?)config.Disks ?? [];
        var usable = disks
            .OfType<DiskProfile>()
            .Where(profile => !string.IsNullOrEmpty(profile.MountPoint))
            .ToList();

        if (config.Disks is null || usable.Count != config.Disks.Count)
        {
            Logger.LogWarning("Ignored invalid disk entries in the settings file");

            // The next save replaces the file without them, so keep the original for repair.
            PreserveUnreadableFile();
            config.Disks = usable;
        }

        return config;
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
    /// <returns>
    /// <c>true</c> if the updated configuration was written; <c>false</c> if the update was
    /// skipped or the write failed (logged). Callers that merely persist state a completed
    /// operation already changed can ignore it; a user-initiated "save settings" should report it.
    /// </returns>
    public bool Update(Func<AppConfiguration, AppConfiguration> change)
    {
        if (_loadFellBackAfterIoFailure)
        {
            // Callers build the new configuration from in-memory state that started as the
            // defaults <see cref="Load"/> fell back to, so writing it would wipe the real file.
            Logger.LogWarning("Skipped a settings update because the settings could not be read at startup");
            return false;
        }

        for (var attempt = 0; ; attempt++)
        {
            // Lock is reentrant, so Load/Save taking it again on this thread is fine.
            lock (_ioLock)
            {
                var current = Read(out var ioFailed, out var retryable);
                if (!ioFailed)
                {
                    var updated = change(current);
                    try
                    {
                        Save(updated);
                        return true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Persisting is best-effort: the caller's operation (e.g. a mount) has
                        // already happened and must not be reported as failed because of it.
                        Logger.LogWarning(ex, "Failed to save settings to '{SettingsPath}'", _settingsPath);
                        return false;
                    }
                }

                // An access-denied is normally permanent, so only a lock-style I/O error is retried.
                if (!retryable || attempt >= UpdateReadRetries)
                {
                    // The existing file is intact but unreadable; saving the defaults read in its
                    // place would wipe the user's settings. The caller's in-memory state is
                    // written by the next update instead.
                    Logger.LogWarning("Skipped a settings update because '{SettingsPath}' could not be read", _settingsPath);
                    return false;
                }
            }

            // A lock from antivirus or a backup tool is usually brief. The wait is outside the
            // store's lock so other threads aren't held up behind it.
            Thread.Sleep(UpdateReadRetryDelay);
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
            // The same bad file is read again by every Load/Update until a save replaces it, so
            // copying it each time would pile up identical backups.
            if (HasIdenticalBackup())
            {
                return;
            }

            // Two different bad files within the same second must not overwrite each other.
            for (var suffix = 1; File.Exists(backupPath); suffix++)
            {
                backupPath = $"{_settingsPath}.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{suffix}";
            }

            File.Copy(_settingsPath, backupPath, overwrite: false);
            Logger.LogWarning("Copied unreadable settings file to '{BackupPath}'", backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Failed to copy unreadable settings file to '{BackupPath}'", backupPath);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when an existing <c>.corrupt</c> backup has exactly the same bytes as
    /// the current settings file.
    /// </summary>
    /// <returns><c>true</c> if the current file is already backed up.</returns>
    private bool HasIdenticalBackup()
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        var current = File.ReadAllBytes(_settingsPath);

        foreach (var backup in Directory.EnumerateFiles(directory, $"{Path.GetFileName(_settingsPath)}.corrupt-*"))
        {
            if (new FileInfo(backup).Length == current.Length && File.ReadAllBytes(backup).AsSpan().SequenceEqual(current))
            {
                return true;
            }
        }

        return false;
    }
}
