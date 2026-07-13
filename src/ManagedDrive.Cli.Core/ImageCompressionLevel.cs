namespace ManagedDrive.Cli.Core;

/// <summary>
/// Compression level for the <c>mount --compression</c> CLI option. This is a standalone copy of
/// <c>ManagedDrive.Core.ImageCompressionLevel</c> — <c>Cli.Core</c> must not reference
/// <c>ManagedDrive.Core</c> (that project pulls in <c>winfsp.net</c> and <c>SharpCompress</c>,
/// which the pipe-only <c>mdrive.exe</c> client has no use for). Keep the explicit values in sync
/// with the Core enum; the App layer casts between the two at the CLI/app boundary.
/// </summary>
public enum ImageCompressionLevel
{
    None = 0,
    Fastest = 1,
    Optimal = 2,
    SmallestSize = 3,
}