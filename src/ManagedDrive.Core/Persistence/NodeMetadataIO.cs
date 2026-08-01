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
    public readonly record struct NodeMetadata(string Path, Fsp.Interop.FileInfo FileInfo, byte[]? Security);

    public static void WriteMetadata(BinaryWriter writer, string path, FileNode node)
    {
        writer.Write(path);
        writer.Write(node.FileInfo.FileAttributes);
        writer.Write(node.FileInfo.AllocationSize);
        writer.Write(node.FileInfo.FileSize);
        writer.Write(node.FileInfo.CreationTime);
        writer.Write(node.FileInfo.LastAccessTime);
        writer.Write(node.FileInfo.LastWriteTime);
        writer.Write(node.FileInfo.ChangeTime);
        writer.Write(node.FileInfo.IndexNumber);
        writer.Write(node.FileInfo.HardLinks);

        var security = node.FileSecurity ?? [];
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
        var security = secLen > 0 ? reader.ReadBytes(secLen) : null;

        return new NodeMetadata(path, fileInfo, security);
    }
}
