namespace ManagedDrive.App.Services;

/// <summary>
/// Reads file sizes for the startup progress display without ever failing the caller.
/// </summary>
public static class FileSizeProbe
{
    /// <summary>
    /// Gets the size of a file, or <c>null</c> if it doesn't exist or can't be inspected. Like
    /// <see cref="File.Exists"/> it never throws for a malformed or inaccessible path, so one
    /// damaged profile can't stop the startup auto-mount.
    /// </summary>
    /// <param name="path">The file path; may be <c>null</c>.</param>
    /// <returns>The length in bytes, or <c>null</c> if unknown.</returns>
    public static ulong? TryGetSize(string? path)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (ulong)file.Length : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
