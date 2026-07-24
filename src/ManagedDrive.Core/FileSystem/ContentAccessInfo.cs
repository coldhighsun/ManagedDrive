namespace ManagedDrive.Core.FileSystem;

/// <summary>
/// An immutable snapshot of a single content read or write: when it happened and which file
/// it touched. Always replaced as a whole via <c>Interlocked.Exchange</c> so readers never
/// observe a time/path pair from two different accesses.
/// </summary>
public sealed record ContentAccessInfo(DateTimeOffset Time, string Path);