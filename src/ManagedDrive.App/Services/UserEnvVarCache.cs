using ThrottledLogging;

namespace ManagedDrive.App.Services;

/// <summary>
/// Caches the current user's environment variables for a short time. The disk cards ask, for every
/// disk on every refresh tick (including while the window is hidden in the tray), which variables
/// point into the disk, and reading the registry for each of them would add up. Writers that
/// change the environment call <see cref="Invalidate"/> so the change is visible immediately.
/// </summary>
/// <param name="read">Reads one variable from the system.</param>
/// <param name="tickCount">Returns a monotonic tick count in milliseconds.</param>
/// <param name="duration">How long a read is reused.</param>
internal sealed class UserEnvVarCache(Func<string, string?> read, Func<long> tickCount, TimeSpan duration)
{
    /// <summary>
    /// Logs variables that could not be read.
    /// </summary>
    private static readonly ILogger Logger = AppLog.CreateLogger<UserEnvVarCache>();

    /// <summary>
    /// Serializes cache access; refresh ticks and the writers that invalidate can be on different threads.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The values last read, by variable name; refreshed in place when the cache expires.
    /// </summary>
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tick count at which <see cref="_values"/> expire; 0 means nothing is cached.
    /// </summary>
    private long _expiresAt;

    /// <summary>
    /// Bumped whenever a cached value changed or the cache was invalidated, so a caller can tell nothing differs since it last looked.
    /// </summary>
    private long _generation;

    /// <summary>
    /// Gets the cache shared by all disks, reading the user's variables for 5 seconds at a time.
    /// </summary>
    public static UserEnvVarCache Shared { get; } = new(
        ReadUserVariable,
        () => Environment.TickCount64,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Reads a variable from the user's environment. Runs from the refresh tick, so a failure to
    /// read counts as "not set" instead of escaping into the UI thread.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The variable's value, or <c>null</c> if it isn't set or can't be read.</returns>
    private static string? ReadUserVariable(string name)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            Logger.LogWarningThrottled(
                $"user-env-read:{name}", TimeSpan.FromMinutes(10),
                "Could not read the user environment variable {Variable}: {Error}", name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Gets a user variable, reading it again if the cached values have expired or were invalidated.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The variable's value, or <c>null</c> if it isn't set.</returns>
    public string? Get(string name)
    {
        lock (_lock)
        {
            ExpireIfDue();
            if (!_values.TryGetValue(name, out var value))
            {
                value = read(name);
                _values[name] = value;
            }

            return value;
        }
    }

    /// <summary>
    /// Gets a number that changes whenever a cached variable's value changed on a re-read or the
    /// cache was invalidated. Lets a caller skip recomputing something derived from the variables
    /// while nothing differs.
    /// </summary>
    public long Generation
    {
        get
        {
            lock (_lock)
            {
                ExpireIfDue();
                return _generation;
            }
        }
    }

    /// <summary>
    /// Marks every cached value as stale so the next <see cref="Get"/> or <see cref="Generation"/>
    /// re-reads them from the system, and bumps <see cref="Generation"/>.
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _expiresAt = 0;
            _generation++;
        }
    }

    /// <summary>
    /// Re-reads the variables asked for so far once the cache has expired or was invalidated, and
    /// bumps <see cref="_generation"/> only when one of them differs. Call with <see cref="_lock"/> held.
    /// </summary>
    private void ExpireIfDue()
    {
        var now = tickCount();
        if (_expiresAt != 0 && now < _expiresAt)
        {
            return;
        }

        // Read everything before changing anything, so a read that throws leaves the cache stale
        // (and retried on the next call) rather than half refreshed and marked fresh.
        var changed = new List<KeyValuePair<string, string?>>();
        foreach (var (name, previous) in _values)
        {
            var current = read(name);
            if (current != previous)
            {
                changed.Add(new(name, current));
            }
        }

        foreach (var (name, current) in changed)
        {
            _values[name] = current;
        }

        if (changed.Count > 0)
        {
            _generation++;
        }

        _expiresAt = now + (long)duration.TotalMilliseconds;
    }

    /// <summary>
    /// Whether a variable currently points at the folder a redirection names on a disk.
    /// </summary>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <param name="redirect">The redirection to look for.</param>
    /// <returns><c>true</c> when the variable is set to that folder, ignoring case and a trailing slash.</returns>
    public bool PointsInto(string mountPoint, EnvRedirect redirect)
    {
        return EnvRedirectPolicy.IsRedirectedInto(Get(redirect.Variable), mountPoint, redirect);
    }
}
