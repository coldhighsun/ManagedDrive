namespace ManagedDrive.Core;

/// <summary>
/// Serializes and deserializes the contents of an in-memory file system to and from a binary
/// image file so that RAM disk data can survive application restarts.
/// </summary>
/// <remarks>
/// Image format (little-endian binary):
/// <list type="bullet">
///   <item>4-byte magic "MDRD"</item>
///   <item>Int32 version (currently 1)</item>
///   <item>UInt64 capacity in bytes</item>
///   <item>length-prefixed UTF-8 string volume label</item>
///   <item>Int32 node count</item>
///   <item>For each node: path, metadata, security descriptor bytes, file data bytes</item>
/// </list>
/// </remarks>
public static class DiskImageSerializer
{
    private const int Version = 1;
    private static readonly byte[] Magic = "MDRD"u8.ToArray();

    /// <summary>
    /// Reads a disk image from <paramref name="imagePath"/> and returns a populated
    /// <see cref="FileNodeMap"/> along with the stored capacity and volume label.
    /// </summary>
    /// <param name="imagePath">Source image file path.</param>
    /// <param name="capacityBytes">Receives the capacity stored in the image.</param>
    /// <param name="volumeLabel">Receives the volume label stored in the image.</param>
    /// <returns>
    /// A <see cref="FileNodeMap"/> pre-populated with the nodes from the image.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the file does not contain a valid ManagedDrive image or the version is
    /// unsupported.
    /// </exception>
    public static FileNodeMap Load(
        string imagePath,
        out ulong capacityBytes,
        out string volumeLabel)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);

        var magic = reader.ReadBytes(4);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a valid ManagedDrive image file.");
        }

        var version = reader.ReadInt32();
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported image version: {version}.");
        }

        capacityBytes = reader.ReadUInt64();
        volumeLabel = reader.ReadString();

        var nodeMap = new FileNodeMap();
        var count = reader.ReadInt32();

        for (var i = 0; i < count; i++)
        {
            var (path, node) = ReadNode(reader);
            nodeMap.Add(path, node);
        }

        return nodeMap;
    }

    /// <summary>
    /// Writes the full contents of <paramref name="nodeMap"/> to <paramref name="imagePath"/>,
    /// creating or overwriting the file.
    /// </summary>
    /// <param name="nodeMap">Node map to serialize.</param>
    /// <param name="capacityBytes">Configured capacity of the disk in bytes.</param>
    /// <param name="volumeLabel">Volume label string.</param>
    /// <param name="imagePath">Destination file path.</param>
    public static void Save(
        FileNodeMap nodeMap,
        ulong capacityBytes,
        string volumeLabel,
        string imagePath)
    {
        using var stream = new FileStream(imagePath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: false);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(capacityBytes);
        writer.Write(volumeLabel);

        var nodes = new List<KeyValuePair<string, FileNode>>(nodeMap.GetAllNodes());
        writer.Write(nodes.Count);

        foreach (var kvp in nodes)
        {
            WriteNode(writer, kvp.Key, kvp.Value);
        }
    }

    private static (string Path, FileNode Node) ReadNode(BinaryReader reader)
    {
        var path = reader.ReadString();

        var node = new FileNode
        {
            FileInfo =
            {
                FileAttributes = reader.ReadUInt32(),
                AllocationSize = reader.ReadUInt64(),
                FileSize       = reader.ReadUInt64(),
                CreationTime   = reader.ReadUInt64(),
                LastAccessTime = reader.ReadUInt64(),
                LastWriteTime  = reader.ReadUInt64(),
                ChangeTime     = reader.ReadUInt64(),
                IndexNumber    = reader.ReadUInt64(),
                HardLinks      = reader.ReadUInt32(),
            },
        };

        var secLen = reader.ReadInt32();
        if (secLen > 0)
        {
            node.FileSecurity = reader.ReadBytes(secLen);
        }

        var dataLen = reader.ReadInt64();
        if (dataLen > 0 && !node.IsDirectory)
        {
            var fileContent = reader.ReadBytes((int)dataLen);
            var aligned = FileNode.AlignToAllocationUnit(node.FileInfo.AllocationSize);
            node.FileData = new byte[aligned];
            Buffer.BlockCopy(
                fileContent, 0,
                node.FileData, 0,
                Math.Min(fileContent.Length, node.FileData.Length));
        }
        else if (dataLen > 0)
        {
            // Skip data bytes for directories (should not occur in well-formed images)
            reader.ReadBytes((int)dataLen);
        }

        return (path, node);
    }

    private static void WriteNode(BinaryWriter writer, string path, FileNode node)
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

        var security = node.FileSecurity ?? Array.Empty<byte>();
        writer.Write(security.Length);
        writer.Write(security);

        if (node is { IsDirectory: false, FileData: not null, FileInfo.FileSize: > 0 })
        {
            var fileSize = Math.Min(node.FileInfo.FileSize, (ulong)node.FileData.Length);
            writer.Write((long)fileSize);
            writer.Write(node.FileData, 0, (int)fileSize);
        }
        else
        {
            writer.Write(0L);
        }
    }
}