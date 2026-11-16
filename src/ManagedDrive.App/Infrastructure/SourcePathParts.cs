namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Pure helper that splits a disk's source path (an archive or image file) for display on its card.
/// </summary>
public static class SourcePathParts
{
    /// <summary>
    /// Splits a path into its file name and the folder it is in.
    /// </summary>
    /// <param name="path">A file path, or <c>null</c>/empty when the disk has no source.</param>
    /// <returns>
    /// The file name and the folder; both are empty for a missing path, and the folder is empty when the path is just a name.
    /// A trailing separator is ignored.
    /// </returns>
    public static (string FileName, string Directory) Split(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (string.Empty, string.Empty);
        }

        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        var directory = Path.GetDirectoryName(trimmed) ?? string.Empty;
        return (name, directory);
    }
}
