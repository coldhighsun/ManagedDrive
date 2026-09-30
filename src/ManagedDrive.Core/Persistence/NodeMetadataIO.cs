namespace ManagedDrive.Core.Persistence;

/// <summary>
/// Shared binary read/write logic for the <c>path + <see cref="Fsp.Interop.FileInfo"/> + security
/// descriptor</c> portion of a node record. Extracted because <see cref="DiskImageSerializer"/> and
/// <see cref="Snapshots.SnapshotStore"/> write this exact layout byte-for-byte identically before
/// diverging on how the file's data is stored (inline bytes vs. a content-addressed blob hash).
/// Do not extend this beyond metadata into data/blob territory - the two formats are fundamentally
/// different there.
/// </summary>
internal static class NodeMetadataIO
{
    /// <summary>
    /// Largest security-descriptor length accepted when reading a node record.
    /// </summary>
    private const int MaxSecurityDescriptorBytes = 1024 * 1024;

    public readonly record struct NodeMetadata(string Path, Fsp.Interop.FileInfo FileInfo, byte[]? Security);

    public static void WriteMetadata(BinaryWriter writer, string path, FileNode node) =>
        WriteMetadata(writer, path, node.FileInfo, node.FileSecurity);

    /// <summary>
    /// Writes a metadata record from an already-captured <paramref name="fileInfo"/>, for callers
    /// that must record sizes matching the exact bytes they go on to store rather than whatever
    /// the live node reports by the time the record is written.
    /// </summary>
    public static void WriteMetadata(BinaryWriter writer, string path, in Fsp.Interop.FileInfo fileInfo, byte[]? security)
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
    }

    public static NodeMetadata ReadMetadata(BinaryReader reader)
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

        return new(path, fileInfo, security);
    }
}
