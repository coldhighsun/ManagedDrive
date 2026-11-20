namespace ManagedDrive.Core.Archive;

/// <summary>
/// What <see cref="ArchiveNodeMapBuilder.BuildNodeMap"/> noticed while importing, beyond the node map itself.
/// </summary>
public sealed class ArchiveImportReport
{
    /// <summary>
    /// Gets how many file entries were merged into an earlier entry whose name differs only by
    /// case, because the disk is case-insensitive. The later entry's content replaced the earlier one.
    /// </summary>
    public int CaseMergedCount { get; private set; }

    /// <summary>
    /// Gets the first merged pair (the entry kept first, then the entry that replaced it), or
    /// <c>null</c> when nothing was merged.
    /// </summary>
    public (string First, string Second)? FirstCaseMerge { get; private set; }

    /// <summary>
    /// Records one merge.
    /// </summary>
    /// <param name="first">The path of the entry that was already there.</param>
    /// <param name="second">The path of the entry that replaced it.</param>
    internal void AddCaseMerge(string first, string second)
    {
        CaseMergedCount++;
        FirstCaseMerge ??= (first, second);
    }
}
