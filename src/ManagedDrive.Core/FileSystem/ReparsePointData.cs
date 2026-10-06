using System.Buffers.Binary;
using Fsp;

namespace ManagedDrive.Core.FileSystem;

/// <summary>
/// Validation helpers for the <c>REPARSE_DATA_BUFFER</c> blobs of the symbolic links and directory
/// junctions the file system supports. Pure managed code, so it works without the WinFsp native
/// library (unlike <see cref="FileSystemBase.CanReplaceReparsePoint"/>).
/// </summary>
internal static class ReparsePointData
{
    /// <summary>
    /// <c>IO_REPARSE_TAG_SYMLINK</c>: a symbolic link to a file or directory.
    /// </summary>
    internal const uint SymlinkTag = 0xA000000C;

    /// <summary>
    /// <c>IO_REPARSE_TAG_MOUNT_POINT</c>: a directory junction.
    /// </summary>
    internal const uint MountPointTag = 0xA0000003;

    /// <summary>
    /// Size of the fixed <c>REPARSE_DATA_BUFFER</c> header: tag (4), data length (2), reserved (2).
    /// </summary>
    internal const int HeaderLength = 8;

    /// <summary>
    /// <c>MAXIMUM_REPARSE_DATA_BUFFER_SIZE</c>: Windows never accepts a larger reparse buffer, so
    /// neither the file system nor the image loader allocates for one.
    /// </summary>
    internal const int MaxLength = 16 * 1024;

    /// <summary>
    /// Reads the reparse tag from the start of a reparse buffer.
    /// </summary>
    /// <param name="data">A reparse buffer of at least <see cref="HeaderLength"/> bytes.</param>
    /// <returns>The reparse tag.</returns>
    internal static uint GetTag(byte[] data) => BinaryPrimitives.ReadUInt32LittleEndian(data);

    /// <summary>
    /// Returns whether <paramref name="tag"/> is one of the tags the file system supports.
    /// </summary>
    /// <param name="tag">The reparse tag to test.</param>
    /// <returns><c>true</c> for a symbolic link or junction tag.</returns>
    internal static bool IsSupportedTag(uint tag) => tag is SymlinkTag or MountPointTag;

    /// <summary>
    /// Validates a reparse buffer handed to <c>SetReparsePoint</c>.
    /// </summary>
    /// <param name="data">The buffer to validate.</param>
    /// <returns>
    /// <c>STATUS_SUCCESS</c>; <c>STATUS_IO_REPARSE_DATA_INVALID</c> when the buffer is malformed
    /// (too small or too large, or its declared data length disagrees with its size); or
    /// <c>STATUS_IO_REPARSE_TAG_INVALID</c> when the tag is not supported.
    /// </returns>
    internal static int Validate(byte[]? data)
    {
        if (data is null || data.Length < HeaderLength || data.Length > MaxLength)
        {
            return FileSystemBase.STATUS_IO_REPARSE_DATA_INVALID;
        }

        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        if (declaredLength != data.Length - HeaderLength)
        {
            return FileSystemBase.STATUS_IO_REPARSE_DATA_INVALID;
        }

        return IsSupportedTag(GetTag(data))
            ? FileSystemBase.STATUS_SUCCESS
            : FileSystemBase.STATUS_IO_REPARSE_TAG_INVALID;
    }

    /// <summary>
    /// Checks that an existing reparse point may be replaced or deleted by a request carrying
    /// <paramref name="requested"/>; Windows requires the tags to match.
    /// </summary>
    /// <param name="current">The reparse buffer currently on the node.</param>
    /// <param name="requested">The buffer supplied with the request.</param>
    /// <returns>
    /// <c>STATUS_SUCCESS</c> or <c>STATUS_IO_REPARSE_TAG_MISMATCH</c>.
    /// </returns>
    internal static int CheckReplaceable(byte[] current, byte[] requested)
    {
        return requested.Length >= HeaderLength && GetTag(current) == GetTag(requested)
            ? FileSystemBase.STATUS_SUCCESS
            : FileSystemBase.STATUS_IO_REPARSE_TAG_MISMATCH;
    }
}
