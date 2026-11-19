namespace ManagedDrive.App.Services;

/// <summary>
/// What a call to <see cref="TrayAlertTracker"/> changed.
/// </summary>
/// <param name="ChangedKeys">The disks (or the system key) whose reasons changed.</param>
/// <param name="NewCritical">Whether a critical reason was newly set, so the badge should pulse again.</param>
public readonly record struct TrayAlertUpdate(IReadOnlyList<object> ChangedKeys, bool NewCritical)
{
    /// <summary>
    /// Gets an update that changed nothing.
    /// </summary>
    public static TrayAlertUpdate None => new([], false);

    /// <summary>
    /// Gets whether anything changed.
    /// </summary>
    public bool HasChanges => ChangedKeys.Count > 0 || NewCritical;
}

/// <summary>
/// Decides when each tray alert reason starts and ends. Pure state machine: the caller reports what
/// happened, with the time, and applies the returned <see cref="TrayAlertUpdate"/>.
/// <list type="bullet">
/// <item>
/// <description>
/// A refused write is a recent failure, not a state: it stays for <see cref="RefusalHold"/> after the
/// last refusal of its kind on that disk, and every new refusal starts the hold again. The disk being
/// nearly full stays visible through the high-usage warning. Freed space is deliberately not the end
/// condition: a failed copy deletes its partial file at once, which would clear the alert within seconds.
/// </description>
/// </item>
/// <item>
/// <description>
/// A write refused for low memory also needs memory to have recovered (above
/// <see cref="LowMemoryMonitor.RecoverAboveBytes"/>) before it ends.
/// </description>
/// </item>
/// <item>
/// <description>
/// A failed save lasts until a save succeeds again; high usage follows the disk's own flag.
/// </description>
/// </item>
/// <item>
/// <description>
/// Low system memory is one system-wide warning with the monitor's thresholds as hysteresis, shown only
/// while a disk is mounted.
/// </description>
/// </item>
/// </list>
/// </summary>
public sealed class TrayAlertTracker
{
    /// <summary>
    /// How long a refused write keeps its critical alert after the last refusal.
    /// </summary>
    public static readonly TimeSpan RefusalHold = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The key of the system-wide low-memory warning, which belongs to no disk.
    /// </summary>
    public static readonly object SystemKey = new();

    /// <summary>
    /// The reasons of every disk and of <see cref="SystemKey"/>.
    /// </summary>
    private readonly TrayAlertBoard _board = new();

    /// <summary>
    /// When each disk last refused a write, by refusal reason.
    /// </summary>
    private readonly Dictionary<(object Disk, TrayAlertReasons Reason), DateTimeOffset> _lastRefusal = [];

    /// <summary>
    /// Whether the last memory reading was above <see cref="LowMemoryMonitor.RecoverAboveBytes"/>;
    /// <c>true</c> until a reading says otherwise.
    /// </summary>
    private bool _memoryRecovered = true;

    /// <summary>
    /// Gets the badge level over every disk and the system.
    /// </summary>
    public TrayAlertLevel Level => _board.Level;

    /// <summary>
    /// Gets the reasons of one disk.
    /// </summary>
    /// <param name="disk">The disk.</param>
    /// <returns>Its reasons.</returns>
    public TrayAlertReasons Get(object disk) => _board.Get(disk);

    /// <summary>
    /// Follows a disk's high-usage flag.
    /// </summary>
    /// <param name="disk">The disk.</param>
    /// <param name="isHighUsage">Whether the disk is over its high-usage threshold.</param>
    /// <returns>What changed.</returns>
    public TrayAlertUpdate SetHighUsage(object disk, bool isHighUsage) => Set(disk, TrayAlertReasons.HighUsage, isHighUsage);

    /// <summary>
    /// Records a refused write and (re)starts its <see cref="RefusalHold"/>.
    /// </summary>
    /// <param name="disk">The disk that refused the write.</param>
    /// <param name="lowMemory">Whether it was refused for low memory rather than a full disk.</param>
    /// <param name="now">The current time.</param>
    /// <returns>What changed; a critical reason new to the disk asks the badge to pulse again.</returns>
    public TrayAlertUpdate OnWriteRejected(object disk, bool lowMemory, DateTimeOffset now)
    {
        var reason = lowMemory ? TrayAlertReasons.MemoryRejected : TrayAlertReasons.DiskFull;
        _lastRefusal[(disk, reason)] = now;
        if (lowMemory)
        {
            _memoryRecovered = false;
        }

        return Set(disk, reason, true);
    }

    /// <summary>
    /// Records a failed save of a disk.
    /// </summary>
    /// <param name="disk">The disk.</param>
    /// <returns>What changed.</returns>
    public TrayAlertUpdate OnSaveFailed(object disk) => Set(disk, TrayAlertReasons.SaveFailed, true);

    /// <summary>
    /// Records that a disk saved successfully after a failure.
    /// </summary>
    /// <param name="disk">The disk.</param>
    /// <returns>What changed.</returns>
    public TrayAlertUpdate OnSaveRecovered(object disk) => Set(disk, TrayAlertReasons.SaveFailed, false);

    /// <summary>
    /// Feeds one reading of available memory, which updates the low-memory warning and ends the
    /// refusal alerts whose hold has passed.
    /// </summary>
    /// <param name="availableBytes">Currently available physical memory.</param>
    /// <param name="mountedDisks">How many disks are mounted.</param>
    /// <param name="now">The current time.</param>
    /// <returns>What changed.</returns>
    public TrayAlertUpdate OnMemoryReading(ulong availableBytes, int mountedDisks, DateTimeOffset now)
    {
        if (availableBytes > LowMemoryMonitor.RecoverAboveBytes)
        {
            _memoryRecovered = true;
        }
        else if (availableBytes < LowMemoryMonitor.WarnBelowBytes)
        {
            _memoryRecovered = false;
        }

        var update = TrayAlertUpdate.None;
        if (availableBytes < LowMemoryMonitor.WarnBelowBytes && mountedDisks > 0)
        {
            update = Merge(update, Set(SystemKey, TrayAlertReasons.LowMemory, true));
        }
        else if (_memoryRecovered || mountedDisks == 0)
        {
            update = Merge(update, Set(SystemKey, TrayAlertReasons.LowMemory, false));
        }

        return Merge(update, Expire(now));
    }

    /// <summary>
    /// Ends every refusal alert whose <see cref="RefusalHold"/> has passed, a low-memory refusal
    /// only once memory has recovered as well.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>What changed.</returns>
    public TrayAlertUpdate Expire(DateTimeOffset now)
    {
        var update = TrayAlertUpdate.None;
        foreach (var ((disk, reason), at) in _lastRefusal.ToList())
        {
            if (now - at < RefusalHold || (reason == TrayAlertReasons.MemoryRejected && !_memoryRecovered))
            {
                continue;
            }

            _lastRefusal.Remove((disk, reason));
            update = Merge(update, Set(disk, reason, false));
        }

        return update;
    }

    /// <summary>
    /// Forgets a disk that was unmounted, and the system warning too once no disk is left.
    /// </summary>
    /// <param name="disk">The disk.</param>
    /// <param name="remainingDisks">How many disks are still mounted.</param>
    /// <returns>What changed.</returns>
    public TrayAlertUpdate RemoveDisk(object disk, int remainingDisks)
    {
        foreach (var key in _lastRefusal.Keys.Where(k => ReferenceEquals(k.Disk, disk)).ToList())
        {
            _lastRefusal.Remove(key);
        }

        var update = _board.Remove(disk) ? new TrayAlertUpdate([disk], false) : TrayAlertUpdate.None;
        if (remainingDisks == 0)
        {
            update = Merge(update, Set(SystemKey, TrayAlertReasons.LowMemory, false));
        }

        return update;
    }

    /// <summary>
    /// Sets or clears reasons of one key on the board.
    /// </summary>
    /// <param name="key">A disk or <see cref="SystemKey"/>.</param>
    /// <param name="reasons">The reasons.</param>
    /// <param name="active">Whether to set or clear them.</param>
    /// <returns>What changed.</returns>
    private TrayAlertUpdate Set(object key, TrayAlertReasons reasons, bool active)
    {
        var before = _board.Get(key);
        if (!_board.Set(key, reasons, active))
        {
            return TrayAlertUpdate.None;
        }

        var added = _board.Get(key) & ~before;
        return new([key], (added & TrayAlertRules.CriticalReasons) != 0);
    }

    /// <summary>
    /// Combines two updates.
    /// </summary>
    /// <param name="first">The first update.</param>
    /// <param name="second">The second update.</param>
    /// <returns>The keys of both, without repeats, and whether either brought a new critical reason.</returns>
    private static TrayAlertUpdate Merge(TrayAlertUpdate first, TrayAlertUpdate second) =>
        !second.HasChanges ? first
        : !first.HasChanges ? second
        : new([.. first.ChangedKeys.Union(second.ChangedKeys)], first.NewCritical || second.NewCritical);
}
