namespace ManagedDrive.Core.FileSystem;

/// <summary>
/// Decides whether a growing allocation would eat into <see cref="ReserveBytes"/> of free
/// physical memory, without querying the system on every call.
/// <para>
/// Querying available memory (<c>GlobalMemoryStatusEx</c>) costs ~0.7 µs, which on a small-block
/// streaming append — where nearly every write extends the allocation — was most of the write's
/// cost. Instead, a reading is turned into a headroom budget that each granted allocation is
/// atomically charged against, and reused for up to <see cref="DefaultRefreshMs"/>. A request the
/// cached budget can't cover always falls through to a fresh reading, so a denial is never based
/// on stale data; staleness can only let through memory that something outside this budget
/// (another process) consumed within the refresh window. Freed memory is not credited back; the
/// next refresh picks it up.
/// </para>
/// <para>
/// <see cref="Shared"/> is the one budget every <see cref="MemoryFileSystem"/> uses in production,
/// so disks mounted side by side in this process charge the same headroom rather than each
/// believing it owns all of it. Only a file system given its own provider (tests) gets a private
/// budget.
/// </para>
/// </summary>
internal sealed class MemoryHeadroomBudget(Func<ulong> availableMemoryProvider, long refreshMs = MemoryHeadroomBudget.DefaultRefreshMs)
{
    /// <summary>
    /// Safety margin of physical memory, in bytes, that must remain available after a growing
    /// write for it to be allowed. Growth allocates plain managed <c>byte[]</c> chunks (see
    /// <see cref="FileContent"/>), so nothing else in the system is warned as free memory runs
    /// out; without this reserve, a large enough RAM disk write could push the whole machine
    /// into swapping or an out-of-memory condition before the write itself ever fails.
    /// </summary>
    internal const ulong ReserveBytes = 256UL * 1024 * 1024;

    /// <summary>
    /// How long a provider reading may be reused, in milliseconds. Overridable per instance so
    /// tests can pin the cache open instead of racing a 100 ms window.
    /// </summary>
    internal const long DefaultRefreshMs = 100;

    /// <summary>
    /// The process-wide budget backed by the real system memory counters.
    /// </summary>
    internal static MemoryHeadroomBudget Shared { get; } = new(SystemMemoryInfo.GetAvailablePhysicalBytes);

    /// <summary>
    /// Bytes that may still be allocated before dipping into <see cref="ReserveBytes"/>, as of the
    /// last provider reading minus everything granted since. Negative once exhausted. Only
    /// trusted until <see cref="_expiresAt"/>.
    /// </summary>
    private long _headroom;

    /// <summary>
    /// <see cref="Environment.TickCount64"/> value after which <see cref="_headroom"/> is stale and
    /// must be re-read. Starts at 0 so the first check always queries.
    /// </summary>
    private long _expiresAt;

    /// <summary>
    /// Returns <c>true</c> when allocating <paramref name="extra"/> more bytes would leave less
    /// than <see cref="ReserveBytes"/> of physical memory available system-wide; otherwise charges
    /// <paramref name="extra"/> against the budget and returns <c>false</c>.
    /// </summary>
    /// <remarks>
    /// A thread that refreshes writes its fresh reading over <see cref="_headroom"/>, so charges
    /// other threads made between its provider call and that write are overwritten and go
    /// unaccounted. That window is one provider call (under a microsecond), and the next refresh
    /// re-reads real memory anyway, so nothing drifts beyond it.
    /// </remarks>
    internal bool WouldExceed(ulong extra)
    {
        if (extra == 0)
        {
            return false;
        }

        var charge = (long)Math.Min(extra, long.MaxValue);
        var now = Environment.TickCount64;

        if (now < Volatile.Read(ref _expiresAt) &&
            Interlocked.Add(ref _headroom, -charge) >= 0)
        {
            return false;
        }

        var available = availableMemoryProvider();
        var headroom = available > ReserveBytes
            ? (long)Math.Min(available - ReserveBytes, long.MaxValue)
            : 0;
        var exceeds = headroom < charge;

        Volatile.Write(ref _headroom, exceeds ? headroom : headroom - charge);
        Volatile.Write(ref _expiresAt, now + refreshMs);
        return exceeds;
    }
}
