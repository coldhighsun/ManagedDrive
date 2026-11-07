namespace ManagedDrive.Core.Mounting;

/// <summary>
/// The one definition of "these two file paths name the same file", shared by everything that
/// matches disks, profiles, images and archives against each other.
/// </summary>
public static class FilePathComparison
{
    /// <summary>
    /// Whether two paths name the same file. Two fully qualified paths are compared by their
    /// resolved full path (so <c>C:\a\..\b.mdr</c> equals <c>C:\b.mdr</c>), ignoring case. If
    /// either path is relative the raw strings are compared instead, ignoring case: resolving a
    /// relative path would depend on the process's current directory, and the answer would change
    /// with where the app happens to be started from.
    /// </summary>
    /// <param name="a">The first path, or <c>null</c> for none.</param>
    /// <param name="b">The second path, or <c>null</c> for none.</param>
    /// <returns><c>false</c> when either path is <c>null</c>.</returns>
    public static bool AreSame(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b))
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Fall through to the raw comparison for a path that cannot be resolved.
            }
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
