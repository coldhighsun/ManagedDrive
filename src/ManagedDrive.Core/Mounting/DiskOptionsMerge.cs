namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Merges an edit made on a copy of a disk's options into the options the disk has now, so a
/// field the edit left alone keeps what another change (the CLI, a preset moved to another disk)
/// set while the edit was open.
/// </summary>
public static class DiskOptionsMerge
{
    /// <summary>
    /// Returns <paramref name="current"/> with every field the edit changed taken from
    /// <paramref name="edited"/>: a field whose value in <paramref name="edited"/> differs from
    /// <paramref name="baseline"/> is the edit's, every other one is kept from
    /// <paramref name="current"/>. Folders and redirects are merged entry by entry: what the edit
    /// removed is taken out of the current list and what it added is put in, so an entry another
    /// change added or removed meanwhile keeps that change. <c>null</c> counts as an empty list.
    /// </summary>
    /// <param name="baseline">The options the edit started from.</param>
    /// <param name="edited">The options the edit produced.</param>
    /// <param name="current">The disk's options now.</param>
    /// <returns>The merged options.</returns>
    public static DiskOptions MergeEdit(DiskOptions baseline, DiskOptions edited, DiskOptions current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(current);

        return current with
        {
            CapacityBytes = Pick(baseline.CapacityBytes, edited.CapacityBytes, current.CapacityBytes),
            MountPoint = Pick(baseline.MountPoint, edited.MountPoint, current.MountPoint),
            VolumeLabel = Pick(baseline.VolumeLabel, edited.VolumeLabel, current.VolumeLabel),
            ReadOnly = Pick(baseline.ReadOnly, edited.ReadOnly, current.ReadOnly),
            PersistImagePath = Pick(baseline.PersistImagePath, edited.PersistImagePath, current.PersistImagePath),
            SourceArchivePath = Pick(baseline.SourceArchivePath, edited.SourceArchivePath, current.SourceArchivePath),
            AutoMount = Pick(baseline.AutoMount, edited.AutoMount, current.AutoMount),
            AutoSaveIntervalMinutes = Pick(baseline.AutoSaveIntervalMinutes, edited.AutoSaveIntervalMinutes, current.AutoSaveIntervalMinutes),
            CompressionLevel = Pick(baseline.CompressionLevel, edited.CompressionLevel, current.CompressionLevel),
            MaxSnapshotCount = Pick(baseline.MaxSnapshotCount, edited.MaxSnapshotCount, current.MaxSnapshotCount),
            MaxSnapshotSizeBytes = Pick(baseline.MaxSnapshotSizeBytes, edited.MaxSnapshotSizeBytes, current.MaxSnapshotSizeBytes),
            HighUsageWarnPercent = Pick(baseline.HighUsageWarnPercent, edited.HighUsageWarnPercent, current.HighUsageWarnPercent),
            SaveImageOnExit = Pick(baseline.SaveImageOnExit, edited.SaveImageOnExit, current.SaveImageOnExit),
            // A folder's edit is detected case-sensitively, so renaming "Cache" to "cache" counts,
            // but the entry it replaces is matched in the current list the way Windows matches
            // paths, ignoring case.
            Folders = MergeList(
                baseline.Folders, edited.Folders, current.Folders, StringComparer.Ordinal, StringComparer.OrdinalIgnoreCase, keyOf: null),
            EnvRedirects = MergeList(
                baseline.EnvRedirects,
                edited.EnvRedirects,
                current.EnvRedirects,
                EqualityComparer<EnvRedirect>.Default,
                EqualityComparer<EnvRedirect>.Default,
                keyOf: r => r.Variable),
        };
    }

    /// <summary>
    /// Returns the edit's value when it changed the field, otherwise the current one.
    /// </summary>
    /// <typeparam name="T">The field's type.</typeparam>
    /// <param name="baseline">The value the edit started from.</param>
    /// <param name="edited">The value the edit produced.</param>
    /// <param name="current">The value now.</param>
    /// <returns>The value to keep.</returns>
    private static T Pick<T>(T baseline, T edited, T current) =>
        EqualityComparer<T>.Default.Equals(baseline, edited) ? current : edited;

    /// <summary>
    /// Merges a list entry by entry: <paramref name="current"/> without the entries the edit removed
    /// from <paramref name="baseline"/>, plus the entries it added, in the edit's order. An added
    /// entry replaces any current entry with the same <paramref name="keyOf"/> key (a variable can
    /// point at one folder only). Returns <paramref name="current"/> itself when the edit did not
    /// change the list, and <c>null</c> for an empty result.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="baseline">The list the edit started from.</param>
    /// <param name="edited">The list the edit produced.</param>
    /// <param name="current">The list now.</param>
    /// <param name="diffComparer">Decides which entries the edit removed and added.</param>
    /// <param name="matchComparer">
    /// Matches those entries against <paramref name="current"/>: an entry equal to a removed one
    /// is taken out, and an added one is not put in twice.
    /// </param>
    /// <param name="keyOf">Gets the key only one entry may have, or <c>null</c> when there is none.</param>
    /// <returns>The merged list.</returns>
    private static IReadOnlyList<T>? MergeList<T>(
        IReadOnlyList<T>? baseline,
        IReadOnlyList<T>? edited,
        IReadOnlyList<T>? current,
        IEqualityComparer<T> diffComparer,
        IEqualityComparer<T> matchComparer,
        Func<T, string>? keyOf)
    {
        var before = baseline ?? [];
        var after = edited ?? [];
        if (before.SequenceEqual(after, diffComparer))
        {
            return current;
        }

        var removed = before.Where(item => !after.Contains(item, diffComparer)).ToList();
        var added = after.Where(item => !before.Contains(item, diffComparer)).ToList();
        var addedKeys = keyOf is null
            ? []
            : added.Select(keyOf).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var merged = (current ?? [])
            .Where(item => !removed.Contains(item, matchComparer))
            .Where(item => keyOf is null || !addedKeys.Contains(keyOf(item)))
            .ToList();
        foreach (var item in added)
        {
            if (!merged.Contains(item, matchComparer))
            {
                merged.Add(item);
            }
        }

        return merged.Count == 0 ? null : merged;
    }
}
