namespace ManagedDrive.App.Services;

/// <summary>
/// Lets a notification through at most once per cooldown for each key, so a problem that keeps
/// recurring (every retry of a failed copy, say) is reported once instead of on every occurrence.
/// </summary>
/// <param name="cooldown">How long a key stays quiet after it was let through.</param>
/// <param name="clock">Supplies the current time; replaced in tests. Defaults to the system clock.</param>
public sealed class NotificationCooldown(TimeSpan cooldown, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, DateTimeOffset> _lastAllowed = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Asks whether the notification identified by <paramref name="key"/> may be shown now. A
    /// <c>true</c> answer starts that key's cooldown.
    /// </summary>
    /// <param name="key">Identifies the notification, e.g. the disk and the problem.</param>
    /// <returns><c>true</c> if the cooldown for <paramref name="key"/> has elapsed or never started.</returns>
    public bool TryAcquire(string key)
    {
        var now = _clock();
        lock (_lock)
        {
            if (_lastAllowed.TryGetValue(key, out var last) && now - last < cooldown)
            {
                return false;
            }

            _lastAllowed[key] = now;
            return true;
        }
    }
}
