namespace ManagedDrive.App.Services;

/// <summary>
/// How urgent the alert badge on the tray icon is.
/// </summary>
public enum TrayAlertLevel
{
    /// <summary>
    /// No badge.
    /// </summary>
    None = 0,

    /// <summary>
    /// Something needs watching, but nothing has failed yet: a steady amber badge.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// Something has failed (a refused write or a failed save): a red badge that pulses for a while.
    /// </summary>
    Critical = 2,
}

/// <summary>
/// Why a disk (or the system as a whole) shows an alert on the tray icon. A disk can have several at once.
/// </summary>
[Flags]
public enum TrayAlertReasons
{
    /// <summary>
    /// No alert.
    /// </summary>
    None = 0,

    /// <summary>
    /// The disk is over its high-usage threshold.
    /// </summary>
    HighUsage = 1 << 0,

    /// <summary>
    /// Available system memory is low, before any write was refused.
    /// </summary>
    LowMemory = 1 << 1,

    /// <summary>
    /// A write was refused because the disk is full.
    /// </summary>
    DiskFull = 1 << 2,

    /// <summary>
    /// A write was refused because the system is nearly out of memory.
    /// </summary>
    MemoryRejected = 1 << 3,

    /// <summary>
    /// Saving the disk image failed and has not succeeded since.
    /// </summary>
    SaveFailed = 1 << 4,
}

/// <summary>
/// The rules that turn alert reasons into a badge level.
/// </summary>
public static class TrayAlertRules
{
    /// <summary>
    /// The reasons that mean something has already failed.
    /// </summary>
    public const TrayAlertReasons CriticalReasons =
        TrayAlertReasons.DiskFull | TrayAlertReasons.MemoryRejected | TrayAlertReasons.SaveFailed;

    /// <summary>
    /// Gets the badge level for a set of reasons: critical if any reason is critical, a warning if
    /// there is any other reason, otherwise none.
    /// </summary>
    /// <param name="reasons">The reasons.</param>
    /// <returns>The badge level.</returns>
    public static TrayAlertLevel GetLevel(TrayAlertReasons reasons) =>
        (reasons & CriticalReasons) != 0 ? TrayAlertLevel.Critical
        : reasons != TrayAlertReasons.None ? TrayAlertLevel.Warning
        : TrayAlertLevel.None;
}

/// <summary>
/// Builds the line the tray tooltip shows under a disk with an alert.
/// </summary>
public static class TrayAlertText
{
    /// <summary>
    /// The reasons that get a text, in the order they are listed, with their string keys. High usage
    /// and low system memory have none: the usage bar and the memory footer already show them.
    /// </summary>
    private static readonly (TrayAlertReasons Reason, string Key)[] Entries =
    [
        (TrayAlertReasons.DiskFull, "TrayTip.AlertDiskFull"),
        (TrayAlertReasons.MemoryRejected, "TrayTip.AlertMemoryRejected"),
        (TrayAlertReasons.SaveFailed, "TrayTip.AlertSaveFailed"),
    ];

    /// <summary>
    /// Joins the texts of the reasons that have one.
    /// </summary>
    /// <param name="reasons">The disk's reasons.</param>
    /// <param name="getString">Looks up a localized string by key.</param>
    /// <returns>The texts separated by " · "; empty when none applies.</returns>
    public static string Build(TrayAlertReasons reasons, Func<string, string> getString) =>
        string.Join(" · ", Entries.Where(e => reasons.HasFlag(e.Reason)).Select(e => getString(e.Key)));
}

/// <summary>
/// Collects the alert reasons of every disk (and of the system, under its own key) and reduces them
/// to the one badge level the tray icon shows. Pure bookkeeping; the caller decides what sets and
/// clears a reason.
/// </summary>
public sealed class TrayAlertBoard
{
    /// <summary>
    /// The reasons of each key that has at least one.
    /// </summary>
    private readonly Dictionary<object, TrayAlertReasons> _reasons = [];

    /// <summary>
    /// Gets the badge level over all keys: the most urgent one.
    /// </summary>
    public TrayAlertLevel Level =>
        _reasons.Values.Aggregate(TrayAlertLevel.None, (level, reasons) => (TrayAlertLevel)Math.Max((int)level, (int)TrayAlertRules.GetLevel(reasons)));

    /// <summary>
    /// Gets the reasons of one key.
    /// </summary>
    /// <param name="key">A disk, or the key of a system-wide alert.</param>
    /// <returns>The reasons; <see cref="TrayAlertReasons.None"/> for an unknown key.</returns>
    public TrayAlertReasons Get(object key) => _reasons.GetValueOrDefault(key);

    /// <summary>
    /// Sets or clears reasons of one key.
    /// </summary>
    /// <param name="key">A disk, or the key of a system-wide alert.</param>
    /// <param name="reasons">The reasons to set or clear.</param>
    /// <param name="active">Whether to set (<c>true</c>) or clear (<c>false</c>) them.</param>
    /// <returns>Whether the reasons of the key changed.</returns>
    public bool Set(object key, TrayAlertReasons reasons, bool active)
    {
        var before = Get(key);
        var after = active ? before | reasons : before & ~reasons;
        if (after == before)
        {
            return false;
        }

        if (after == TrayAlertReasons.None)
        {
            _reasons.Remove(key);
        }
        else
        {
            _reasons[key] = after;
        }

        return true;
    }

    /// <summary>
    /// Clears every reason of one key, e.g. when its disk is unmounted.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>Whether the key had any reason.</returns>
    public bool Remove(object key) => _reasons.Remove(key);
}
