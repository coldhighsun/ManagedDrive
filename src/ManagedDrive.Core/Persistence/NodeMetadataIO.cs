namespace ManagedDrive.Core.Persistence;

/// <summary>
/// Shared binary read/write logic for the <c>path + <see cref="Fsp.Interop.FileInfo"/> + security
/// descriptor</c> portion of a node record. Extracted because <see cref="DiskImageSerializer"/> and
/// <see cref="Snapshots.SnapshotStore"/> write this exact layout byte-for-byte identically before
/// diverging on how the file's data is stored (inline bytes vs. a content-addressed blob hash).
/// Do not extend this beyond metadata into data/blob territory - the two formats are fundamentally
/// different there.
/// </summary>
/// <remarks>
/// Formats that postdate reparse-point support (image version 7, snapshot version 2) append one
/// more field to the record: the length-prefixed reparse buffer of a symbolic link or junction
/// (length 0 for an ordinary node). Older layouts omit it; callers pick the layout with
/// <c>includeReparse</c>.
/// </remarks>
internal static class NodeMetadataIO
{
    /// <summary>
    /// Largest security-descriptor length accepted when reading a node record.
    /// </summary>
    private const int MaxSecurityDescriptorBytes = 1024 * 1024;

    /// <summary>
    /// One decoded node metadata record.
    /// </summary>
    /// <param name="Path">The node's full path.</param>
    /// <param name="FileInfo">The node's file metadata.</param>
    /// <param name="Security">The node's security descriptor, or <c>null</c> if it had none.</param>
    /// <param name="ReparseData">
    /// The node's validated reparse buffer, or <c>null</c> for an ordinary node or a layout without
    /// the reparse field.
    /// </param>
    public readonly record struct NodeMetadata(
        string Path,
        Fsp.Interop.FileInfo FileInfo,
        byte[]? Security,
        byte[]? ReparseData = null);

    public static void WriteMetadata(BinaryWriter writer, string path, FileNode node, bool includeReparse = false) =>
        WriteMetadata(writer, path, node.FileInfo, node.FileSecurity, includeReparse, node.ReparseData);

    /// <summary>
    /// Writes a metadata record from an already-captured <paramref name="fileInfo"/>, for callers
    /// that must record sizes matching the exact bytes they go on to store rather than whatever
    /// the live node reports by the time the record is written.
    /// </summary>
    public static void WriteMetadata(
        BinaryWriter writer,
        string path,
        in Fsp.Interop.FileInfo fileInfo,
        byte[]? security,
        bool includeReparse = false,
        byte[]? reparseData = null)
    {
        writer.Write(path);
        writer.Write(fileInfo.FileAttributes);
        writer.Write(fileInfo.AllocationSize);
        writer.Write(fileInfo.FileSize);
        writer.Write(fileInfo.CreationTime);
        writer.Write(fileInfo.LastAccessTime);
        writer.Write(fileInfo.LastWriteTime);
        writer.Write(fileInfo.ChangeTime);
        writer.Write(fileInfo.IndexNumber);
        writer.Write(fileInfo.HardLinks);

        security ??= [];
        writer.Write(security.Length);
        writer.Write(security);

        if (includeReparse)
        {
            reparseData ??= [];
            writer.Write(reparseData.Length);
            writer.Write(reparseData);
        }
    }

    public static NodeMetadata ReadMetadata(BinaryReader reader, bool includeReparse = false)
    {
        var path = reader.ReadString();

        var fileInfo = new Fsp.Interop.FileInfo
        {
            FileAttributes = reader.ReadUInt32(),
            AllocationSize = reader.ReadUInt64(),
            FileSize = reader.ReadUInt64(),
            CreationTime = reader.ReadUInt64(),
            LastAccessTime = reader.ReadUInt64(),
            LastWriteTime = reader.ReadUInt64(),
            ChangeTime = reader.ReadUInt64(),
            IndexNumber = reader.ReadUInt64(),
            HardLinks = reader.ReadUInt32(),
        };

        var secLen = reader.ReadInt32();

        // Real descriptors are a few hundred bytes (each ACL is capped at 64 KiB); the generous
        // bound only stops a corrupt length from allocating an arbitrarily large array up front.
        if (secLen is < 0 or > MaxSecurityDescriptorBytes)
        {
            throw new InvalidDataException($"Invalid security descriptor length: {secLen}.");
        }

        var security = secLen > 0 ? reader.ReadBytes(secLen) : null;
        if (security is not null && security.Length != secLen)
        {
            throw new EndOfStreamException("The stream ends inside a security descriptor.");
        }

        var reparseData = includeReparse ? ReadReparseData(reader, path) : null;

        return new(path, fileInfo, security, reparseData);
    }

    /// <summary>
    /// Reads the length-prefixed reparse buffer that ends a node record in the layouts that have
    /// one, rejecting a length Windows would never produce before allocating for it and a buffer
    /// that isn't a supported symbolic link or junction.
    /// </summary>
    /// <param name="reader">The reader positioned at the reparse length.</param>
    /// <param name="path">The node's path, for the error message.</param>
    /// <returns>The reparse buffer, or <c>null</c> when the length is zero.</returns>
    private static byte[]? ReadReparseData(BinaryReader reader, string path)
    {
        var length = reader.ReadInt32();
        if (length == 0)
        {
            return null;
        }

        if (length is < 0 or > ReparsePointData.MaxLength)
        {
            throw new InvalidDataException($"Invalid reparse data length {length} for '{path}'.");
        }

        var data = reader.ReadBytes(length);
        if (data.Length != length)
        {
            throw new EndOfStreamException("The stream ends inside reparse data.");
        }

        if (ReparsePointData.Validate(data) != Fsp.FileSystemBase.STATUS_SUCCESS)
        {
            throw new InvalidDataException($"Invalid reparse data for '{path}'; the image is corrupted.");
        }

        return data;
    }
}
