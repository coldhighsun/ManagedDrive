namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Thrown by <see cref="RamDisk.Format"/> when the disk's image path now points at a file the
/// caller did not confirm for deletion (the path was edited after the confirmation), so the
/// format stopped before changing anything.
/// </summary>
public sealed class ImagePathChangedException(string imagePath, string message) : IOException(message)
{
    /// <summary>
    /// Gets the image path that already has a file.
    /// </summary>
    public string ImagePath { get; } = imagePath;
}
