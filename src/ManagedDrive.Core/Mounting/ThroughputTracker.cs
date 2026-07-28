namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Converts a cumulative byte counter sampled at arbitrary intervals into an instantaneous
/// bytes/sec rate. Pure logic, independent of WinFsp — the counter and clock are supplied by
/// the caller on every <see cref="Sample"/> call.
/// </summary>
public sealed class ThroughputTracker
{
    private long? _lastBytes;
    private DateTimeOffset? _lastSampleTime;

    /// <summary>
    /// Feeds a new cumulative byte total and returns the instantaneous rate in bytes/sec since
    /// the previous call. The first call for a given instance has no baseline and returns 0.
    /// </summary>
    public double Sample(long cumulativeBytes, DateTimeOffset now)
    {
        if (_lastBytes is not { } lastBytes || _lastSampleTime is not { } lastTime)
        {
            _lastBytes = cumulativeBytes;
            _lastSampleTime = now;
            return 0.0;
        }

        var elapsed = (now - lastTime).TotalSeconds;
        _lastBytes = cumulativeBytes;
        _lastSampleTime = now;

        if (elapsed <= 0)
        {
            return 0.0;
        }

        var delta = cumulativeBytes - lastBytes;
        return delta <= 0 ? 0.0 : delta / elapsed;
    }

    /// <summary>
    /// Discards the current baseline so the next <see cref="Sample"/> call starts fresh.
    /// </summary>
    public void Reset()
    {
        _lastBytes = null;
        _lastSampleTime = null;
    }
}
