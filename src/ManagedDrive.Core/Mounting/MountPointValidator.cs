namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Shared logic for classifying and pre-validating a <see cref="DiskOptions.MountPoint"/> value.
/// A mount point is either a Windows drive letter (<c>X:</c>) or the path of an existing, empty
/// NTFS directory — WinFsp requires the directory form to already exist and be empty before it
/// can be mounted onto, and gives an opaque NTSTATUS failure if it isn't. Extracted out of
/// <see cref="RamDisk"/> so headless callers (the CLI mount path) can give a clear error message
/// before ever attempting the mount, instead of surfacing a raw NTSTATUS code.
/// </summary>
public static class MountPointValidator
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="mountPoint"/> is a Windows drive-letter path of
    /// the form <c>X:</c> (single letter followed by a colon).
    /// </summary>
    public static bool IsDriveLetter(string mountPoint) =>
        mountPoint.Length == 2 && char.IsLetter(mountPoint[0]) && mountPoint[1] == ':';

    /// <summary>
    /// Validates a directory-path mount point before attempting to mount onto it. Drive-letter
    /// mount points are always considered valid here — WinFsp itself reports drive-letter
    /// conflicts, and <see cref="RamDisk.Create"/> already waits for the letter to become visible.
    /// </summary>
    /// <param name="mountPoint">The mount point to validate.</param>
    /// <param name="error">Set to a human-readable message when the method returns <c>false</c>.</param>
    /// <returns>
    /// <c>true</c> if <paramref name="mountPoint"/> is a drive letter, or an existing empty
    /// directory; <c>false</c> otherwise.
    /// </returns>
    public static bool TryValidateDirectoryMountPoint(string mountPoint, out string? error)
    {
        if (IsDriveLetter(mountPoint))
        {
            error = null;
            return true;
        }

        if (!Directory.Exists(mountPoint))
        {
            error = $"Mount point directory does not exist: {mountPoint}";
            return false;
        }

        if (Directory.EnumerateFileSystemEntries(mountPoint).Any())
        {
            error = $"Mount point directory is not empty: {mountPoint}";
            return false;
        }

        error = null;
        return true;
    }
}
