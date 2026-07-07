using System.IO.Compression;

namespace ManagedDrive.Core;

/// <summary>
/// Serializes and deserializes the contents of an in-memory file system to and from a binary
/// image file so that RAM disk data can survive application restarts.
/// </summary>
/// <remarks>
/// Image format (little-endian binary):
/// <list type="bullet">
///   <item>4-byte magic "MDRD"</item>
///   <item>Int32 version (currently 2)</item>
///   <item>Byte holding an <see cref="ImageCompressionLevel"/> value (version 2+ only; absent in version 1, which is always uncompressed)</item>
///   <item>The rest of the payload below, gzip-compressed whenever the level is not <see cref="ImageCompressionLevel.None"/>:</item>
///   <item>UInt64 capacity in bytes</item>
///   <item>length-prefixed UTF-8 string volume label</item>
///   <item>Int32 node count</item>
///   <item>For each node: path, metadata, security descriptor bytes, file data bytes</item>
/// </list>
/// </remarks>
public static class DiskImageSerializer
{
    private const int Version = 2;
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
        if (version is not (1 or 2))
        {
            throw new InvalidDataException($"Unsupported image version: {version}.");
        }

        var compressed = version == 2 && (ImageCompressionLevel)reader.ReadByte() != ImageCompressionLevel.None;

        using var payloadReader = compressed
            ? new(new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true), System.Text.Encoding.UTF8)
            : reader;

        capacityBytes = payloadReader.ReadUInt64();
        volumeLabel = payloadReader.ReadString();

        var nodeMap = new FileNodeMap();
        var count = payloadReader.ReadInt32();

        for (var i = 0; i < count; i++)
        {
            var (path, node) = ReadNode(payloadReader);
            nodeMap.Add(path, node);
        }

        return nodeMap;
    }

    /// <summary>
    /// Reads only the capacity and volume label from <paramref name="imagePath"/> without
    /// loading any file nodes, for cheaply previewing an image before a full <see cref="Load"/>.
    /// </summary>
    /// <param name="imagePath">Source image file path.</param>
    /// <param name="capacityBytes">Receives the capacity stored in the image.</param>
    /// <param name="volumeLabel">Receives the volume label stored in the image.</param>
    /// <exception cref="InvalidDataException">
    /// Thrown when the file does not contain a valid ManagedDrive image or the version is
    /// unsupported.
    /// </exception>
    public static void PeekHeader(string imagePath, out ulong capacityBytes, out string volumeLabel)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);

        var magic = reader.ReadBytes(4);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a valid ManagedDrive image file.");
        }

        var version = reader.ReadInt32();
        if (version is not (1 or 2))
        {
            throw new InvalidDataException($"Unsupported image version: {version}.");
        }

        var compressed = version == 2 && (ImageCompressionLevel)reader.ReadByte() != ImageCompressionLevel.None;

        using var payloadReader = compressed
            ? new(new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true), System.Text.Encoding.UTF8)
            : reader;

        capacityBytes = payloadReader.ReadUInt64();
        volumeLabel = payloadReader.ReadString();
    }

    /// <summary>
    /// Writes the full contents of <paramref name="nodeMap"/> to <paramref name="imagePath"/>,
    /// creating or overwriting the file.
    /// </summary>
    /// <param name="nodeMap">Node map to serialize.</param>
    /// <param name="capacityBytes">Configured capacity of the disk in bytes.</param>
    /// <param name="volumeLabel">Volume label string.</param>
    /// <param name="imagePath">Destination file path.</param>
    /// <param name="level">Compression level applied to the payload; <see cref="ImageCompressionLevel.None"/> disables compression.</param>
    public static void Save(
        FileNodeMap nodeMap,
        ulong capacityBytes,
        string volumeLabel,
        string imagePath,
        ImageCompressionLevel level)
    {
        var compress = level != ImageCompressionLevel.None;
        var directory = Path.GetDirectoryName(imagePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a sibling temp file and flush it to disk before atomically replacing the
        // real image path, so a process kill mid-write (e.g. during a Windows shutdown) never
        // leaves the actual image truncated — worst case is a stray .tmp file.
        var tempPath = imagePath + ".tmp";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                using (var headerWriter = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    headerWriter.Write(Magic);
                    headerWriter.Write(Version);
                    headerWriter.Write((byte)level);
                }

                using (var payloadWriter = new BinaryWriter(
                    compress ? new GZipStream(stream, ToCompressionLevel(level), leaveOpen: true) : stream,
                    System.Text.Encoding.UTF8,
                    leaveOpen: true))
                {
                    payloadWriter.Write(capacityBytes);
                    payloadWriter.Write(volumeLabel);

                    var nodes = nodeMap.GetAllNodes();
                    payloadWriter.Write(nodes.Count);

                    foreach (var kvp in nodes)
                    {
                        WriteNode(payloadWriter, kvp.Key, kvp.Value);
                    }

                    payloadWriter.Flush();
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, imagePath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup of the partial temp file.
            }

            throw;
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

    private static CompressionLevel ToCompressionLevel(ImageCompressionLevel level) => level switch
    {
        ImageCompressionLevel.Fastest => CompressionLevel.Fastest,
        ImageCompressionLevel.SmallestSize => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };

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