namespace ManagedDrive.App.Services;

/// <summary>
/// Caches the current user's <c>TEMP</c> value for a short time. Reading it hits the registry and
/// is needed for every disk on every refresh tick, including while the window is hidden in the
/// tray, so reads are shared; writers that change TEMP call <see cref="Invalidate"/> so the
/// change is visible immediately rather than after the cache expires.
/// </summary>
/// <param name="read">Reads the value from the system.</param>
/// <param name="tickCount">Returns a monotonic tick count in milliseconds.</param>
/// <param name="duration">How long a read is reused.</param>
internal sealed class UserTempCache(Func<string?> read, Func<long> tickCount, TimeSpan duration)
{
    /// <summary>
    /// Serializes cache access; refresh ticks and the writers that invalidate can be on different threads.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The value last read.
    /// </summary>
    private string? _value;

    /// <summary>
    /// Tick count at which <see cref="_value"/> expires; 0 means nothing is cached.
    /// </summary>
    private long _expiresAt;

    /// <summary>
    /// Gets the cache shared by all disks, reading the user's TEMP for 5 seconds at a time.
    /// </summary>
    public static UserTempCache Shared { get; } = new(
        () => Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User),
        () => Environment.TickCount64,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Gets the user's TEMP value, reading it again if the cached one has expired or was invalidated.
    /// </summary>
    /// <returns>The user's TEMP value, or <c>null</c> if it isn't set.</returns>
    public string? Get()
    {
        lock (_lock)
        {
            var now = tickCount();
            if (_expiresAt == 0 || now >= _expiresAt)
            {
                _value = read();
                _expiresAt = now + (long)duration.TotalMilliseconds;
            }

            return _value;
        }
    }

    /// <summary>
    /// Discards the cached value so the next <see cref="Get"/> reads the system again.
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _expiresAt = 0;
        }
    }
}
