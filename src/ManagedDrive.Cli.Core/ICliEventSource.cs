namespace ManagedDrive.Cli.Core;

/// <summary>
/// Where the <c>watch</c> command gets its events from. Kept apart from
/// <see cref="ICliDiskController"/>: the controller answers one request, this one pushes events
/// for as long as a client stays connected.
/// </summary>
public interface ICliEventSource
{
    /// <summary>
    /// Starts delivering events to <paramref name="handler"/> until the returned subscription is
    /// disposed. The handler may be called from any thread and must not block.
    /// </summary>
    /// <param name="handler">Receives each event as it happens.</param>
    /// <returns>The subscription; disposing it stops the delivery.</returns>
    Task<IDisposable> SubscribeAsync(Action<CliEvent> handler);
}

/// <summary>
/// The names of the events <c>watch</c> reports.
/// </summary>
public static class CliEventNames
{
    /// <summary>
    /// A disk finished mounting.
    /// </summary>
    public const string Mounted = "mounted";

    /// <summary>
    /// A disk was unmounted.
    /// </summary>
    public const string Unmounted = "unmounted";

    /// <summary>
    /// A disk's image was saved.
    /// </summary>
    public const string SaveCompleted = "save-completed";

    /// <summary>
    /// Saving a disk's image failed.
    /// </summary>
    public const string SaveFailed = "save-failed";

    /// <summary>
    /// A disk's usage crossed its high-usage warning threshold.
    /// </summary>
    public const string HighUsage = "high-usage";
}

/// <summary>
/// Something that happened to a mounted disk, as reported by <c>watch</c>.
/// </summary>
/// <param name="Event">What happened; one of <see cref="CliEventNames"/>.</param>
/// <param name="MountPoint">The disk's mount point, e.g. <c>R:</c>.</param>
/// <param name="Time">When it happened.</param>
/// <param name="Message">Detail such as a volume label or an error message, if the event has any.</param>
public sealed record CliEvent(string Event, string MountPoint, DateTimeOffset Time, string? Message = null)
{
    /// <summary>
    /// Renders the event as one human-readable line: local time, event name, mount point, then any detail.
    /// </summary>
    /// <returns>The line, without a line break.</returns>
    public string ToText()
    {
        var detail = string.IsNullOrEmpty(Message) ? string.Empty : $" {SingleLine(Message)}";
        return $"{Time.ToLocalTime():HH:mm:ss} {Event} {MountPoint}{detail}";
    }

    /// <summary>
    /// Renders the event as one line of JSON, for newline-delimited consumers.
    /// </summary>
    /// <returns>The JSON document, without a line break.</returns>
    public string ToJson() => CliJson.SerializeLine(this);

    /// <summary>
    /// Replaces line breaks so a multi-line error message still occupies one output line.
    /// </summary>
    /// <param name="text">The text to flatten.</param>
    /// <returns><paramref name="text"/> with each run of line breaks replaced by a space.</returns>
    private static string SingleLine(string text) =>
        text.ReplaceLineEndings(" ").Trim();
}
