using System.Text.Json;

namespace ManagedDrive.App.Services;

/// <summary>
/// Keeps the list of active environment-variable redirections (<see cref="EnvRedirectBackup"/>) in
/// a small file of its own next to the settings, so a damaged or unreadable <c>settings.json</c>
/// cannot take with it the only record of what the variables held before.
/// </summary>
public sealed class EnvRedirectBackupStore
{
    /// <summary>
    /// Logger for read and write failures.
    /// </summary>
    private static readonly ILogger Logger = AppLog.CreateLogger<EnvRedirectBackupStore>();

    /// <summary>
    /// Serializer options.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Full path of the backup file.
    /// </summary>
    private readonly string _path;

    /// <summary>
    /// Serializes reads and writes within the process.
    /// </summary>
    private readonly Lock _ioLock = new();

    /// <summary>
    /// Initializes the store against a file path, creating its directory if needed.
    /// </summary>
    /// <param name="path">Full path of the backup file.</param>
    public EnvRedirectBackupStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _path = path;
    }

    /// <summary>
    /// Reads the stored backups. A missing file means there are none; an unparseable one is copied
    /// aside (so it can be inspected) and treated as empty.
    /// </summary>
    /// <returns>The backups, empty when there are none or the file cannot be read.</returns>
    public IReadOnlyList<EnvRedirectBackup> Read()
    {
        lock (_ioLock)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return [];
                }

                var json = File.ReadAllText(_path);
                try
                {
                    return JsonSerializer.Deserialize<List<EnvRedirectBackup>>(json, JsonOptions)?.OfType<EnvRedirectBackup>().ToList() ?? [];
                }
                catch (JsonException ex)
                {
                    Logger.LogWarning(ex, "The environment redirection backup file is unreadable; moving it aside and starting empty.");
                    File.Move(_path, $"{_path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", overwrite: true);
                    return [];
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(ex, "Could not read the environment redirection backup file.");
                return [];
            }
        }
    }

    /// <summary>
    /// Replaces the stored backups. Written to a temporary file first and swapped into place, so an
    /// interrupted write leaves the old or the new file, never a truncated one.
    /// </summary>
    /// <param name="backups">The backups to store.</param>
    /// <returns><c>false</c> when the file could not be written.</returns>
    public bool Write(IReadOnlyList<EnvRedirectBackup> backups)
    {
        lock (_ioLock)
        {
            try
            {
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(backups, JsonOptions));
                File.Move(temp, _path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(ex, "Could not write the environment redirection backup file.");
                return false;
            }
        }
    }
}
