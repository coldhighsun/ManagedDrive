namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Counts and deletes the files under a directory of a mounted disk without following junctions or
/// symbolic links. <see cref="SearchOption.AllDirectories"/> descends into a directory link and
/// yields the files of its target, which can lie outside the disk, so a per-file delete driven by
/// it would remove files the disk only points at. <see cref="Directory.Delete(string, bool)"/> with
/// <c>recursive</c> does not follow links but throws <see cref="UnauthorizedAccessException"/> on
/// a nested junction, which is why <see cref="DeleteDirectory"/> walks the tree itself. Neither the
/// framework call nor a plain <see cref="File.Delete(string)"/> removes read-only files and
/// directories, so the walk clears that attribute first, which also keeps a folder from being left
/// half deleted.
/// </summary>
internal static class LinkSafeFileTree
{
    /// <summary>
    /// Enumeration options for one directory level that return everything in it, hidden, system
    /// and link entries included.
    /// </summary>
    private static readonly EnumerationOptions LevelOptions = new()
    {
        AttributesToSkip = 0,
    };

    /// <summary>
    /// Counts the files under <paramref name="directoryPath"/>, recursively, as
    /// <see cref="DeleteDirectory"/> would delete them: file links count as files, directory links
    /// count as nothing and are not entered.
    /// </summary>
    /// <param name="directoryPath">The directory to count in; when it is itself a link the result is 0.</param>
    /// <returns>The number of files.</returns>
    public static int CountFiles(string directoryPath) => IsLink(directoryPath) ? 0 : CountEntries(directoryPath);

    /// <summary>
    /// Deletes <paramref name="directoryPath"/> and everything in it. A junction or symbolic link
    /// found on the way (or given as the directory itself) is removed as a link only; what it
    /// points to is left untouched. Read-only files and directories are deleted as well.
    /// </summary>
    /// <param name="directoryPath">The directory, or link to a directory, to delete.</param>
    /// <param name="onFileDeleted">Called after each file (or file link) has been deleted, or <c>null</c>.</param>
    /// <param name="cancellationToken">Checked before each file is deleted.</param>
    public static void DeleteDirectory(string directoryPath, Action? onFileDeleted, CancellationToken cancellationToken)
    {
        if (!IsLink(directoryPath))
        {
            DeleteEntries(directoryPath, onFileDeleted, cancellationToken);
            ClearReadOnly(new DirectoryInfo(directoryPath));
        }

        Directory.Delete(directoryPath);
    }

    /// <summary>
    /// Returns whether <paramref name="path"/> is a reparse point (junction or symbolic link).
    /// </summary>
    /// <param name="path">The file or directory to test.</param>
    /// <returns><c>true</c> if it is a reparse point; <c>false</c> otherwise, including when it does not exist.</returns>
    public static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Counts the files in <paramref name="directoryPath"/>, which is known not to be a link, going
    /// by the attributes the enumeration already returned instead of asking for them again.
    /// </summary>
    /// <param name="directoryPath">The directory to count in.</param>
    /// <returns>The number of files.</returns>
    private static int CountEntries(string directoryPath)
    {
        var count = 0;
        foreach (var entry in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos("*", LevelOptions))
        {
            if (!entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                count++;
            }
            else if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                count += CountEntries(entry.FullName);
            }
        }

        return count;
    }

    /// <summary>
    /// Deletes everything inside <paramref name="directoryPath"/>, which is known not to be a link,
    /// leaving the directory itself.
    /// </summary>
    /// <param name="directoryPath">The directory to empty.</param>
    /// <param name="onFileDeleted">Called after each file (or file link) has been deleted, or <c>null</c>.</param>
    /// <param name="cancellationToken">Checked before each entry is deleted.</param>
    private static void DeleteEntries(string directoryPath, Action? onFileDeleted, CancellationToken cancellationToken)
    {
        // Materialised first so deleting does not disturb the enumeration.
        var entries = new DirectoryInfo(directoryPath).EnumerateFileSystemInfos("*", LevelOptions).ToList();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                ClearReadOnly(entry);
                entry.Delete();
                onFileDeleted?.Invoke();
                continue;
            }

            if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeleteEntries(entry.FullName, onFileDeleted, cancellationToken);
                ClearReadOnly(entry);
            }

            // A directory link is removed as the link it is; its target is never entered.
            Directory.Delete(entry.FullName);
        }
    }

    /// <summary>
    /// Clears the read-only attribute of <paramref name="entry"/>, which would otherwise make its
    /// deletion fail. Links are left alone: setting attributes on one without opening the link
    /// itself would change its target, which may lie outside the disk.
    /// </summary>
    /// <param name="entry">The file or directory about to be deleted.</param>
    private static void ClearReadOnly(FileSystemInfo entry)
    {
        var attributes = entry.Attributes;
        if (!attributes.HasFlag(FileAttributes.ReadOnly) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return;
        }

        // An attribute set of 0 is not valid; Normal stands for "none".
        var cleared = attributes & ~FileAttributes.ReadOnly;
        entry.Attributes = cleared == 0 ? FileAttributes.Normal : cleared;
    }
}
