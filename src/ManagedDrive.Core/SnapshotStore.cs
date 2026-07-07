using System.IO.Compression;
using System.Security.Cryptography;

namespace ManagedDrive.Core;

/// <summary>
/// Reads and writes the content-addressable snapshot storage format: a small per-snapshot
/// index file (magic <c>"MDRS"</c>) referencing file content by SHA-256 hash into a shared
/// blob directory, so identical file content is stored only once across all snapshots of a
/// given main image. Unlike <see cref="DiskImageSerializer"/> (which this format deliberately
/// does not touch or share code with), the index itself is not gzip-compressed as a whole;
/// only individual blobs are compressed.
/// </summary>
internal static class SnapshotStore
{
    private const int Version = 1;
    private static readonly byte[] Magic = "MDRS"u8.ToArray();

    /// <summary>
    /// Cheap summary of a snapshot index file: its total logical (pre-dedup, uncompressed)
    /// content size, and the set of blob hashes (lowercase hex) it references.
    /// </summary>
    internal readonly record struct SnapshotSummary(long LogicalSizeBytes, IReadOnlySet<string> ReferencedHashesHex);

    /// <summary>
    /// Returns the shared blob directory for snapshots of <paramref name="mainImagePath"/>.
    /// </summary>
    internal static string ComputeBlobDirectory(string mainImagePath)
    {
        var directory = Path.GetDirectoryName(mainImagePath);
        var baseName = Path.GetFileNameWithoutExtension(mainImagePath);
        return Path.Combine(directory ?? string.Empty, baseName + ".snapblobs");
    }

    /// <summary>
    /// Returns the on-disk path for the blob with the given content hash, sharded into a
    /// 2-character subfolder to avoid an unbounded flat directory.
    /// </summary>
    internal static string HashToBlobPath(string blobDirectory, byte[] hash)
    {
        var hex = Convert.ToHexStringLower(hash);
        return Path.Combine(blobDirectory, hex[..2], hex + ".blob");
    }

    /// <summary>
    /// Writes <paramref name="nodeMap"/> as a snapshot index file at <paramref name="indexPath"/>,
    /// writing any not-yet-seen file content to <paramref name="blobDirectory"/> as a
    /// content-addressed blob.
    /// </summary>
    internal static void Write(
        FileNodeMap nodeMap,
        ulong capacityBytes,
        string volumeLabel,
        string indexPath,
        string blobDirectory,
        ImageCompressionLevel level)
    {
        Directory.CreateDirectory(blobDirectory);

        var tempPath = indexPath + ".tmp";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Magic);
                    writer.Write(Version);
                    writer.Write((byte)0); // reserved

                    writer.Write(capacityBytes);
                    writer.Write(volumeLabel);

                    var nodes = nodeMap.GetAllNodes();
                    writer.Write(nodes.Count);

                    foreach (var kvp in nodes)
                    {
                        WriteNode(writer, kvp.Key, kvp.Value, blobDirectory, level);
                    }

                    writer.Flush();
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, indexPath, overwrite: true);
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

    /// <summary>
    /// Reads the snapshot index file at <paramref name="indexPath"/>, resolving every
    /// referenced blob from <paramref name="blobDirectory"/>, and returns a populated
    /// <see cref="FileNodeMap"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Thrown when the index file is not a valid snapshot, its version is unsupported, or a
    /// referenced blob is missing or corrupt.
    /// </exception>
    internal static FileNodeMap Load(
        string indexPath,
        string blobDirectory,
        out ulong capacityBytes,
        out string volumeLabel)
    {
        using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);

        ReadHeader(reader);

        capacityBytes = reader.ReadUInt64();
        volumeLabel = reader.ReadString();

        var nodeMap = new FileNodeMap();
        var count = reader.ReadInt32();

        for (var i = 0; i < count; i++)
        {
            var (path, header) = ReadNodeHeader(reader);
            var node = new FileNode
            {
                FileInfo = header.ToFileInfo(),
                FileSecurity = header.Security,
            };

            if (!node.IsDirectory)
            {
                var marker = reader.ReadByte();
                if (marker == 1)
                {
                    var hash = reader.ReadBytes(32);
                    node.FileData = ReadBlob(blobDirectory, hash, path, header.FileSize, header.AllocationSize);
                }
                else
                {
                    node.FileData = new byte[FileNode.AlignToAllocationUnit(header.AllocationSize)];
                }
            }

            nodeMap.Add(path, node);
        }

        return nodeMap;
    }

    /// <summary>
    /// Reads only the header and per-node hash markers of the snapshot index at
    /// <paramref name="indexPath"/>, without reading any blob content.
    /// </summary>
    internal static SnapshotSummary ReadSummary(string indexPath)
    {
        using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);

        ReadHeader(reader);

        _ = reader.ReadUInt64(); // capacityBytes
        _ = reader.ReadString(); // volumeLabel

        var count = reader.ReadInt32();
        long logicalSize = 0;
        var referenced = new HashSet<string>();

        for (var i = 0; i < count; i++)
        {
            var (_, header) = ReadNodeHeader(reader);
            logicalSize += (long)header.FileSize;

            var isDirectory = (header.FileAttributes & (uint)FileAttributes.Directory) != 0;
            if (!isDirectory)
            {
                var marker = reader.ReadByte();
                if (marker == 1)
                {
                    var hash = reader.ReadBytes(32);
                    referenced.Add(Convert.ToHexStringLower(hash));
                }
            }
        }

        return new SnapshotSummary(logicalSize, referenced);
    }

    private static void ReadHeader(BinaryReader reader)
    {
        var magic = reader.ReadBytes(4);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a valid ManagedDrive snapshot file.");
        }

        var version = reader.ReadInt32();
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported snapshot version: {version}.");
        }

        _ = reader.ReadByte(); // reserved
    }

    private readonly record struct NodeHeader(
        uint FileAttributes,
        ulong AllocationSize,
        ulong FileSize,
        ulong CreationTime,
        ulong LastAccessTime,
        ulong LastWriteTime,
        ulong ChangeTime,
        ulong IndexNumber,
        uint HardLinks,
        byte[]? Security)
    {
        public Fsp.Interop.FileInfo ToFileInfo() => new()
        {
            FileAttributes = FileAttributes,
            AllocationSize = AllocationSize,
            FileSize = FileSize,
            CreationTime = CreationTime,
            LastAccessTime = LastAccessTime,
            LastWriteTime = LastWriteTime,
            ChangeTime = ChangeTime,
            IndexNumber = IndexNumber,
            HardLinks = HardLinks,
        };
    }

    private static (string Path, NodeHeader Header) ReadNodeHeader(BinaryReader reader)
    {
        var path = reader.ReadString();

        var header = new NodeHeader(
            FileAttributes: reader.ReadUInt32(),
            AllocationSize: reader.ReadUInt64(),
            FileSize: reader.ReadUInt64(),
            CreationTime: reader.ReadUInt64(),
            LastAccessTime: reader.ReadUInt64(),
            LastWriteTime: reader.ReadUInt64(),
            ChangeTime: reader.ReadUInt64(),
            IndexNumber: reader.ReadUInt64(),
            HardLinks: reader.ReadUInt32(),
            Security: null);

        var secLen = reader.ReadInt32();
        var security = secLen > 0 ? reader.ReadBytes(secLen) : null;

        return (path, header with { Security = security });
    }

    private static void WriteNode(BinaryWriter writer, string path, FileNode node, string blobDirectory, ImageCompressionLevel level)
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

        if (node.IsDirectory)
        {
            return;
        }

        if (node.FileInfo.FileSize == 0 || node.FileData is null)
        {
            writer.Write((byte)0); // EmptyFile marker
            return;
        }

        var fileSize = (int)Math.Min(node.FileInfo.FileSize, (ulong)node.FileData.Length);
        var hash = SHA256.HashData(node.FileData.AsSpan(0, fileSize));
        EnsureBlobWritten(blobDirectory, hash, node.FileData, fileSize, level);

        writer.Write((byte)1); // HasBlob marker
        writer.Write(hash);
    }

    private static void EnsureBlobWritten(string blobDirectory, byte[] hash, byte[] data, int length, ImageCompressionLevel level)
    {
        var blobPath = HashToBlobPath(blobDirectory, hash);
        if (File.Exists(blobPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);

        var compress = level != ImageCompressionLevel.None;
        var tempPath = blobPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                stream.WriteByte(compress ? (byte)1 : (byte)0);

                if (compress)
                {
                    using var gzip = new GZipStream(stream, ToCompressionLevel(level), leaveOpen: true);
                    gzip.Write(data, 0, length);
                }
                else
                {
                    stream.Write(data, 0, length);
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, blobPath, overwrite: false);
        }
        catch (IOException)
        {
            // Another writer already created this content-addressed blob; its bytes are
            // equivalent modulo compression, so no correctness issue. Clean up our temp file.
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static byte[] ReadBlob(string blobDirectory, byte[] hash, string nodePath, ulong fileSize, ulong allocationSize)
    {
        var blobPath = HashToBlobPath(blobDirectory, hash);
        if (!File.Exists(blobPath))
        {
            throw new InvalidDataException(
                $"Snapshot blob missing for '{nodePath}' (hash {Convert.ToHexStringLower(hash)}); the snapshot is incomplete or corrupted.");
        }

        using var stream = new FileStream(blobPath, FileMode.Open, FileAccess.Read);
        var flag = stream.ReadByte();

        byte[] content;
        if (flag == 1)
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            gzip.CopyTo(decompressed);
            content = decompressed.ToArray();
        }
        else
        {
            using var raw = new MemoryStream();
            stream.CopyTo(raw);
            content = raw.ToArray();
        }

        if ((ulong)content.Length != fileSize)
        {
            throw new InvalidDataException(
                $"Snapshot blob for '{nodePath}' (hash {Convert.ToHexStringLower(hash)}) has unexpected length " +
                $"{content.Length} bytes; expected {fileSize}. The snapshot may be corrupted.");
        }

        var aligned = FileNode.AlignToAllocationUnit(allocationSize);
        var fileData = new byte[aligned];
        Buffer.BlockCopy(content, 0, fileData, 0, content.Length);
        return fileData;
    }

    private static CompressionLevel ToCompressionLevel(ImageCompressionLevel level) => level switch
    {
        ImageCompressionLevel.Fastest => CompressionLevel.Fastest,
        ImageCompressionLevel.SmallestSize => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };
}
