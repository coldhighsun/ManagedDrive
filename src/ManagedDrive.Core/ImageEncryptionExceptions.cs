namespace ManagedDrive.Core;

/// <summary>
/// Thrown by <see cref="DiskImageSerializer.Load"/> when the image is password-protected but no
/// password was supplied.
/// </summary>
public sealed class ImagePasswordRequiredException : Exception
{
    public ImagePasswordRequiredException()
        : base("This image is password-protected; a password is required to load it.")
    {
    }
}

/// <summary>
/// Thrown by <see cref="DiskImageSerializer.Load"/> when a password was supplied but does not
/// match the one the image was encrypted with.
/// </summary>
public sealed class ImagePasswordIncorrectException : Exception
{
    public ImagePasswordIncorrectException()
        : base("The supplied password is incorrect.")
    {
    }
}
