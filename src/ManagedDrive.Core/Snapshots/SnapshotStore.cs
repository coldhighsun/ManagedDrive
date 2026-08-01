using System.IO.Compression;
using System.Security.Cryptography;

namespace ManagedDrive.Core.Snapshots;

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
    private const int BlobFlagCompressed = 0b001;
    private const int BlobFlagEncrypted = 0b010;

    /// <summary>
    /// Marks a blob's encrypted payload as chunked AES-256-GCM (see <see cref="ChunkedGcm"/>)
    /// rather than the legacy whole-blob single-shot layout. Only meaningful when
    /// <see cref="BlobFlagEncrypted"/> is also set. Never set for blobs written before this
    /// flag existed, so old blobs keep loading via the legacy branch in <see cref="ReadBlob"/>.
    /// </summary>
    private const int BlobFlagChunked = 0b100;

    private const int BlobNonceSize = 12;
    private const int BlobTagSize = 16;
    private const int Version = 1;
    private static readonly byte[] Magic = "MDRS"u8.ToArray();

    /// <summary>
    /// Cheap summary of a snapshot index file: its total logical (pre-dedup, uncompressed)
    /// content size, and the set of blob hashes (lowercase hex) it references.
    /// </summary>
    internal readonly record struct SnapshotSummary(long LogicalSizeBytes, IReadOnlySet<string> ReferencedHashesHex);

    /// <summary>
    /// One node's identity within a snapshot, as read cheaply from the index without touching
    /// any blob: its path, whether it is a directory, its logical file size, and (for files)
    /// the SHA-256 hash of its content.
    /// </summary>
    internal readonly record struct SnapshotEntry(string Path, bool IsDirectory, ulong FileSize, byte[]? Hash);

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
        out string volumeLabel,
        byte[]? cek)
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
                    node.FileData = ReadBlob(blobDirectory, hash, path, header.FileSize, header.AllocationSize, cek);
                }
                else
                {
                    node.FileData = FileContent.CreateZeroed(FileNode.AlignToAllocationUnit(header.AllocationSize));
                }
            }

            nodeMap.Add(path, node);
        }

        return nodeMap;
    }

    /// <summary>
    /// Reads every node's path, directory flag, logical size, and (for files) content hash from
    /// the snapshot index at <paramref name="indexPath"/>, without reading any blob content.
    /// </summary>
    internal static List<SnapshotEntry> ReadEntries(string indexPath)
    {
        using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);

        ReadHeader(reader);

        _ = reader.ReadUInt64(); // capacityBytes
        _ = reader.ReadString(); // volumeLabel

        var count = reader.ReadInt32();
        var entries = new List<SnapshotEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var (path, header) = ReadNodeHeader(reader);
            var isDirectory = (header.FileAttributes & (uint)FileAttributes.Directory) != 0;

            byte[]? hash = null;
            if (!isDirectory)
            {
                var marker = reader.ReadByte();
                if (marker == 1)
                {
                    hash = reader.ReadBytes(32);
                }
            }

            entries.Add(new(path, isDirectory, header.FileSize, hash));
        }

        return entries;
    }

    /// <summary>
    /// Reads only the header and per-node hash markers of the snapshot index at
    /// <paramref name="indexPath"/>, without reading any blob content.
    /// </summary>
    internal static SnapshotSummary ReadSummary(string indexPath)
    {
        long logicalSize = 0;
        var referenced = new HashSet<string>();

        foreach (var entry in ReadEntries(indexPath))
        {
            logicalSize += (long)entry.FileSize;
            if (entry.Hash is not null)
            {
                referenced.Add(Convert.ToHexStringLower(entry.Hash));
            }
        }

        return new(logicalSize, referenced);
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
        ImageCompressionLevel level,
        byte[]? cek,
        IProgress<double>? progress = null)
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

                    if (nodes.Count == 0)
                    {
                        progress?.Report(1.0);
                    }
                    else
                    {
                        var written = 0;
                        foreach (var kvp in nodes)
                        {
                            WriteNode(writer, kvp.Key, kvp.Value, blobDirectory, level, cek);
                            written++;
                            progress?.Report((double)written / nodes.Count);
                        }
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
    /// Writes the blob for <paramref name="hash"/> if it doesn't already exist. Streams
    /// <paramref name="data"/> straight from <see cref="FileContent"/> through gzip compression
    /// and (when <paramref name="cek"/> is set) chunked AES-256-GCM encryption directly into the
    /// destination file — no whole-file buffer is ever materialized, so a single blob's size is
    /// not limited by <see cref="MemoryStream"/>'s ~2 GB cap. New encrypted blobs always use the
    /// chunked layout (<see cref="ChunkedGcm"/>, flagged via <see cref="BlobFlagChunked"/>); see
    /// <see cref="ReadBlob"/> for the legacy whole-blob layout this format replaces.
    /// </summary>
    private static void EnsureBlobWritten(string blobDirectory, byte[] hash, FileContent data, long length, ImageCompressionLevel level, byte[]? cek)
    {
        var blobPath = HashToBlobPath(blobDirectory, hash);
        if (File.Exists(blobPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);

        var compress = level != ImageCompressionLevel.None;
        var flag = (compress ? BlobFlagCompressed : 0) | (cek is not null ? BlobFlagEncrypted | BlobFlagChunked : 0);

        var tempPath = blobPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                stream.WriteByte((byte)flag);

                Stream target = stream;
                ChunkedGcm.WriteStream? chunkedStream = null;
                if (cek is not null)
                {
                    var baseNonce = RandomNumberGenerator.GetBytes(BlobNonceSize);
                    stream.Write(baseNonce);
                    chunkedStream = new ChunkedGcm.WriteStream(stream, cek, baseNonce, ChunkedGcm.ChunkSize);
                    target = chunkedStream;
                }

                if (compress)
                {
                    var gzip = new GZipStream(target, level.ToDotNetCompressionLevel(), leaveOpen: true);
                    try
                    {
                        data.CopyTo(gzip, length);
                    }
                    finally
                    {
                        // Explicitly disposed (rather than relying on leaveOpen semantics further
                        // up the chain) so the deflate stream's final block is flushed before the
                        // chunked encryption below is completed.
                        gzip.Dispose();
                    }
                }
                else
                {
                    data.CopyTo(target, length);
                }

                chunkedStream?.Complete();
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

    /// <summary>
    /// Reads the blob for <paramref name="hash"/> straight into a <see cref="FileContent"/> via
    /// <see cref="FileContent.FillFromStream"/>, decrypting (chunked or legacy whole-blob, see
    /// <see cref="EnsureBlobWritten"/>) and decompressing on the fly rather than materializing the
    /// ciphertext, plaintext, and decompressed bytes as three separate whole-file buffers.
    /// </summary>
    private static FileContent ReadBlob(string blobDirectory, byte[] hash, string nodePath, ulong fileSize, ulong allocationSize, byte[]? cek)
    {
        var blobPath = HashToBlobPath(blobDirectory, hash);
        if (!File.Exists(blobPath))
        {
            throw new InvalidDataException(
                $"Snapshot blob missing for '{nodePath}' (hash {Convert.ToHexStringLower(hash)}); the snapshot is incomplete or corrupted.");
        }

        using var stream = new FileStream(blobPath, FileMode.Open, FileAccess.Read);
        var flag = stream.ReadByte();
        var compressed = (flag & BlobFlagCompressed) != 0;
        var encrypted = (flag & BlobFlagEncrypted) != 0;
        var chunked = (flag & BlobFlagChunked) != 0;

        Stream plaintextStream;
        byte[]? legacyPlaintext = null;

        if (!encrypted)
        {
            plaintextStream = stream;
        }
        else
        {
            if (cek is null)
            {
                throw new ImagePasswordRequiredException();
            }

            if (chunked)
            {
                var baseNonce = new byte[BlobNonceSize];
                stream.ReadExactly(baseNonce);
                plaintextStream = new ChunkedGcm.ReadStream(stream, cek, baseNonce);
            }
            else
            {
                // Legacy whole-blob layout: a single AES-256-GCM ciphertext covering the entire
                // (already gzip-compressed) payload. Kept only so pre-existing blobs keep loading.
                var nonce = new byte[BlobNonceSize];
                stream.ReadExactly(nonce);
                var tag = new byte[BlobTagSize];
                stream.ReadExactly(tag);

                using var cipherStream = new MemoryStream();
                stream.CopyTo(cipherStream);
                var ciphertext = cipherStream.ToArray();

                var plaintext = new byte[ciphertext.Length];
                try
                {
                    using var aesGcm = new AesGcm(cek, BlobTagSize);
                    aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
                }
                catch (CryptographicException)
                {
                    throw new ImagePasswordIncorrectException();
                }

                legacyPlaintext = plaintext;
                plaintextStream = new MemoryStream(plaintext, writable: false);
            }
        }

        var sourceStream = compressed
            ? new GZipStream(plaintextStream, CompressionMode.Decompress)
            : plaintextStream;

        var aligned = FileNode.AlignToAllocationUnit(allocationSize);
        var content = FileContent.CreateZeroed(aligned);
        long filled;

        try
        {
            filled = content.FillFromStream(sourceStream, (long)fileSize);
        }
        catch (CryptographicException)
        {
            throw new ImagePasswordIncorrectException();
        }
        finally
        {
            if (compressed)
            {
                sourceStream.Dispose();
            }

            if (legacyPlaintext is not null)
            {
                CryptographicOperations.ZeroMemory(legacyPlaintext);
            }
        }

        if ((ulong)filled != fileSize)
        {
            throw new InvalidDataException(
                $"Snapshot blob for '{nodePath}' (hash {Convert.ToHexStringLower(hash)}) has unexpected length " +
                $"{filled} bytes; expected {fileSize}. The snapshot may be corrupted.");
        }

        return content;
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
        var metadata = NodeMetadataIO.ReadMetadata(reader);
        var fileInfo = metadata.FileInfo;

        var header = new NodeHeader(
            FileAttributes: fileInfo.FileAttributes,
            AllocationSize: fileInfo.AllocationSize,
            FileSize: fileInfo.FileSize,
            CreationTime: fileInfo.CreationTime,
            LastAccessTime: fileInfo.LastAccessTime,
            LastWriteTime: fileInfo.LastWriteTime,
            ChangeTime: fileInfo.ChangeTime,
            IndexNumber: fileInfo.IndexNumber,
            HardLinks: fileInfo.HardLinks,
            Security: metadata.Security);

        return (metadata.Path, header);
    }

    private static void WriteNode(BinaryWriter writer, string path, FileNode node, string blobDirectory, ImageCompressionLevel level, byte[]? cek)
    {
        NodeMetadataIO.WriteMetadata(writer, path, node);

        if (node.IsDirectory)
        {
            return;
        }

        if (node.FileInfo.FileSize == 0 || node.FileData is null)
        {
            writer.Write((byte)0); // EmptyFile marker
            return;
        }

        var fileSize = (long)Math.Min(node.FileInfo.FileSize, (ulong)node.FileData.Length);

        byte[] hash;
        using (var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            node.FileData.HashInto(incrementalHash, fileSize);
            hash = incrementalHash.GetHashAndReset();
        }

        EnsureBlobWritten(blobDirectory, hash, node.FileData, fileSize, level, cek);

        writer.Write((byte)1); // HasBlob marker
        writer.Write(hash);
    }
}