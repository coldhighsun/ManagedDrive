namespace ManagedDrive.App.Services;

/// <summary>
/// What <see cref="LowMemoryMonitor.Evaluate"/> found.
/// </summary>
public enum LowMemoryTransition
{
    /// <summary>
    /// Nothing to report.
    /// </summary>
    None = 0,

    /// <summary>
    /// Available memory just fell low enough to warn the user.
    /// </summary>
    Warn = 1,

    /// <summary>
    /// Available memory recovered after having been low.
    /// </summary>
    Recovered = 2,
}

/// <summary>
/// Decides when the system's available physical memory is low enough to warn about, before the
/// RAM disks' own low-memory guard starts refusing writes (it keeps 256 MB free). Pure state
/// machine: the caller supplies the reading and the time.
/// </summary>
public sealed class LowMemoryMonitor
{
    /// <summary>
    /// Available memory, in bytes, below which a warning is raised. Twice the write guard's
    /// reserve, so the warning comes while there is still room to free memory or save work.
    /// </summary>
    public const ulong WarnBelowBytes = 512UL * 1024 * 1024;

    /// <summary>
    /// Available memory, in bytes, that must be exceeded before the low state ends. Higher than
    /// <see cref="WarnBelowBytes"/> so a reading hovering around the threshold doesn't flip back
    /// and forth.
    /// </summary>
    public const ulong RecoverAboveBytes = 768UL * 1024 * 1024;

    /// <summary>
    /// Minimum time between two warnings, even if memory recovered and fell again in between.
    /// </summary>
    public static readonly TimeSpan WarnCooldown = TimeSpan.FromMinutes(10);

    private DateTimeOffset? _lastWarned;
    private bool _isLow;

    /// <summary>
    /// Feeds one reading to the monitor.
    /// </summary>
    /// <param name="availableBytes">Currently available physical memory.</param>
    /// <param name="now">The time of the reading.</param>
    /// <returns>
    /// <see cref="LowMemoryTransition.Warn"/> when memory fell below <see cref="WarnBelowBytes"/>
    /// and no warning was shown within <see cref="WarnCooldown"/>;
    /// <see cref="LowMemoryTransition.Recovered"/> when it rose above <see cref="RecoverAboveBytes"/>
    /// after being low; otherwise <see cref="LowMemoryTransition.None"/>.
    /// </returns>
    public LowMemoryTransition Evaluate(ulong availableBytes, DateTimeOffset now)
    {
        if (_isLow)
        {
            if (availableBytes <= RecoverAboveBytes)
            {
                return LowMemoryTransition.None;
            }

            _isLow = false;
            return LowMemoryTransition.Recovered;
        }

        if (availableBytes >= WarnBelowBytes)
        {
            return LowMemoryTransition.None;
        }

        _isLow = true;
        if (_lastWarned is { } last && now - last < WarnCooldown)
        {
            return LowMemoryTransition.None;
        }

        _lastWarned = now;
        return LowMemoryTransition.Warn;
    }
}
