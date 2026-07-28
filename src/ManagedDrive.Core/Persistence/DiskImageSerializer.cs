using System.IO.Compression;
using System.Security.Cryptography;

namespace ManagedDrive.Core.Persistence;

/// <summary>
/// Encryption parameters for <see cref="DiskImageSerializer.Save"/>: a password used to wrap the
/// content-encryption key (CEK), and the CEK itself. Callers (<see cref="RamDisk"/>) generate the
/// CEK once when encryption is first enabled and reuse it across saves — only the password (and
/// therefore the wrapping) changes when the user changes their password, so previously written
/// nodes/snapshot blobs (encrypted under the same CEK) remain decryptable without re-encryption.
/// </summary>
public readonly record struct ImageEncryptionInfo(string Password, byte[] Cek);

/// <summary>
/// Serializes and deserializes the contents of an in-memory file system to and from a binary
/// image file so that RAM disk data can survive application restarts.
/// </summary>
/// <remarks>
/// Image format (little-endian binary):
/// <list type="bullet">
///   <item>4-byte magic "MDRD"</item>
///   <item>Int32 version (currently 4)</item>
///   <item>Byte holding an <see cref="ImageCompressionLevel"/> value (version 2+ only; absent in version 1, which is always uncompressed)</item>
///   <item>Byte IsEncrypted (version 3+ only; absent/false in earlier versions)</item>
///   <item>UInt64 capacity in bytes (always plaintext, so callers can preview it without a password)</item>
///   <item>length-prefixed UTF-8 string volume label (always plaintext, same reason)</item>
///   <item>
///     When encrypted (version 3+): Salt(16), PBKDF2 iterations (Int32), key-wrap nonce (12),
///     key-wrap tag (16), wrapped content-encryption key (32) — the password-derived key only
///     wraps this randomly generated CEK; actual data is always encrypted with the CEK, so
///     changing the password never requires re-encrypting existing data.
///   </item>
///   <item>
///     Version 3 (legacy, still readable): data nonce (12), data tag (16), then the entire
///     remaining file is one AES-256-GCM ciphertext blob wrapping the whole gzip-compressed
///     node region. Requires materializing the whole node region as a single byte array on
///     both save and load, which is capped by <see cref="AesGcm"/>'s single-shot API and by
///     managed-array limits at roughly 2 GB — kept only so old images keep loading.
///   </item>
///   <item>
///     Version 4 (current) when encrypted: a random 12-byte base nonce, then a sequence of
///     chunks, each independently AES-256-GCM encrypted so no single buffer needs to hold the
///     whole node region. Each chunk is [Int32 ciphertext length][16-byte tag][ciphertext
///     bytes], terminated by a zero-length chunk. Per-chunk nonces are derived from the base
///     nonce by XOR-ing its last 4 bytes with the big-endian chunk index, guaranteeing a unique
///     nonce per chunk under the same key/base nonce (see <see cref="DeriveChunkNonce"/>).
///   </item>
///   <item>When not encrypted (any version): the node region follows directly, gzip-compressed whenever the level is not <see cref="ImageCompressionLevel.None"/>, streamed straight from/to the file rather than buffered.</item>
///   <item>Node region contents: Int32 node count, then for each node: path, metadata, security descriptor bytes, file data bytes</item>
/// </list>
/// </remarks>
public static class DiskImageSerializer
{
    private const int CekSize = 32;
    private const int NonceSize = 12;
    private const int Pbkdf2Iterations = 210_000;
    private const int SaltSize = 16;
    private const int TagSize = 16;
    private const int Version = 4;
    private static readonly byte[] Magic = "MDRD"u8.ToArray();

    /// <summary>
    /// Size of each independently AES-GCM-encrypted chunk when writing a version-4 encrypted
    /// node region. Kept well under 2 GB so no single chunk buffer approaches managed-array or
    /// <see cref="AesGcm"/> single-shot limits. Overridable by tests via <see cref="TestChunkSizeOverride"/>
    /// to exercise the multi-chunk path without allocating a real 64 MB buffer.
    /// </summary>
    private const int DefaultChunkSize = 64 * 1024 * 1024;

    /// <summary>
    /// Test-only override for <see cref="DefaultChunkSize"/>; <see langword="null"/> means use the
    /// production default. Set via <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static int? TestChunkSizeOverride;

    private static int ChunkSize => TestChunkSizeOverride ?? DefaultChunkSize;

    /// <summary>
    /// Generates a fresh random 256-bit content-encryption key for use when encryption is first
    /// enabled on a disk.
    /// </summary>
    public static byte[] GenerateCek() => RandomNumberGenerator.GetBytes(CekSize);

    /// <summary>
    /// Reads a disk image from <paramref name="imagePath"/> and returns a populated
    /// <see cref="FileNodeMap"/> along with the stored capacity and volume label.
    /// </summary>
    /// <param name="imagePath">Source image file path.</param>
    /// <param name="capacityBytes">Receives the capacity stored in the image.</param>
    /// <param name="volumeLabel">Receives the volume label stored in the image.</param>
    /// <param name="password">
    /// Password to unlock the image, or <see langword="null"/> if it is not encrypted.
    /// </param>
    /// <param name="cek">
    /// Receives the unwrapped content-encryption key when the image is encrypted, so the caller
    /// (<see cref="RamDisk"/>) can reuse it for subsequent saves/snapshots without re-deriving it
    /// from the password. <see langword="null"/> when the image is not encrypted.
    /// </param>
    /// <returns>
    /// A <see cref="FileNodeMap"/> pre-populated with the nodes from the image.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the file does not contain a valid ManagedDrive image or the version is
    /// unsupported.
    /// </exception>
    /// <exception cref="ImagePasswordRequiredException">
    /// Thrown when the image is encrypted but <paramref name="password"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ImagePasswordIncorrectException">
    /// Thrown when <paramref name="password"/> does not match the one the image was encrypted with.
    /// </exception>
    public static FileNodeMap Load(
        string imagePath,
        out ulong capacityBytes,
        out string volumeLabel,
        string? password,
        out byte[]? cek)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);

        ReadHeader(reader, out var version, out var level, out var isEncrypted);
        cek = null;

        return version <= 2
            ? LoadLegacy(stream, reader, version, level, out capacityBytes, out volumeLabel)
            : LoadCurrent(stream, reader, version, level, isEncrypted, password, out capacityBytes, out volumeLabel, out cek);
    }

    /// <summary>
    /// Reads only the capacity, volume label and encryption status from <paramref name="imagePath"/>
    /// without loading any file nodes and without requiring a password, for cheaply previewing an
    /// image before a full <see cref="Load"/>.
    /// </summary>
    /// <param name="imagePath">Source image file path.</param>
    /// <param name="capacityBytes">Receives the capacity stored in the image.</param>
    /// <param name="volumeLabel">Receives the volume label stored in the image.</param>
    /// <param name="isEncrypted">Receives whether the image is password-protected.</param>
    /// <exception cref="InvalidDataException">
    /// Thrown when the file does not contain a valid ManagedDrive image or the version is
    /// unsupported.
    /// </exception>
    public static void PeekHeader(
        string imagePath,
        out ulong capacityBytes,
        out string volumeLabel,
        out bool isEncrypted)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);

        ReadHeader(reader, out var version, out var level, out isEncrypted);

        if (version <= 2)
        {
            // Legacy layout: capacity/label are inside the optionally compressed payload.
            var compressed = version == 2 && level != ImageCompressionLevel.None;
            using var payloadReader = compressed
                ? new BinaryReader(new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true), System.Text.Encoding.UTF8)
                : reader;

            capacityBytes = payloadReader.ReadUInt64();
            volumeLabel = payloadReader.ReadString();
        }
        else
        {
            // Version 3+ layout: capacity/label are always plaintext header fields.
            capacityBytes = reader.ReadUInt64();
            volumeLabel = reader.ReadString();
        }
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
    /// <param name="encryption">
    /// Password/content-encryption-key pair to protect the image, or <see langword="null"/>
    /// to save unencrypted.
    /// </param>
    /// <param name="progress">
    /// Optional progress reporter, updated with a fraction in [0, 1] as each node is written.
    /// The subsequent gzip compression and (when encrypting) AES-256-GCM chunk encryption happen
    /// as nodes stream through and are not individually reported.
    /// </param>
    public static void Save(
        FileNodeMap nodeMap,
        ulong capacityBytes,
        string volumeLabel,
        string imagePath,
        ImageCompressionLevel level,
        ImageEncryptionInfo? encryption = null,
        IProgress<double>? progress = null)
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
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Magic);
                    writer.Write(Version);
                    writer.Write((byte)level);
                    writer.Write((byte)(encryption is not null ? 1 : 0));
                    writer.Write(capacityBytes);
                    writer.Write(volumeLabel);

                    if (encryption is { } enc)
                    {
                        var salt = RandomNumberGenerator.GetBytes(SaltSize);
                        var wrappedCek = WrapCek(enc.Cek, enc.Password, salt, Pbkdf2Iterations, out var wrapNonce,
                            out var wrapTag);
                        writer.Write(salt);
                        writer.Write(Pbkdf2Iterations);
                        writer.Write(wrapNonce);
                        writer.Write(wrapTag);
                        writer.Write(wrappedCek);

                        var baseNonce = RandomNumberGenerator.GetBytes(NonceSize);
                        writer.Write(baseNonce);
                        writer.Flush();

                        // Node data streams straight into chunked AES-GCM encryption below —
                        // never buffered whole, so there is no ~2 GB ceiling on disk content.
                        using var chunkedStream = new ChunkedGcmWriteStream(stream, enc.Cek, baseNonce, ChunkSize);
                        WriteNodeRegion(chunkedStream, compress, level, nodeMap, progress);
                        chunkedStream.Complete();
                    }
                    else
                    {
                        writer.Flush();
                        WriteNodeRegion(stream, compress, level, nodeMap, progress);
                    }
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

    /// <summary>
    /// Writes the node-count-prefixed node region for <paramref name="nodeMap"/> directly into
    /// <paramref name="target"/>, gzip-compressing on the fly when <paramref name="compress"/> is
    /// set. Never materializes the whole region as a single in-memory buffer, so disk content of
    /// any size can be saved regardless of the ~2 GB limit on <see cref="MemoryStream"/>/managed
    /// arrays. The <see cref="GZipStream"/> (when used) is explicitly disposed here — rather than
    /// relying on <see cref="BinaryWriter"/>'s own disposal with <c>leaveOpen: true</c>, which
    /// would skip it — so the deflate stream's final block/trailer is always flushed before
    /// <paramref name="target"/> is used for anything else.
    /// </summary>
    private static void WriteNodeRegion(
        Stream target,
        bool compress,
        ImageCompressionLevel level,
        FileNodeMap nodeMap,
        IProgress<double>? progress)
    {
        var payloadStream = compress
            ? new GZipStream(target, ToCompressionLevel(level), leaveOpen: true)
            : target;

        try
        {
            using var payloadWriter = new BinaryWriter(payloadStream, System.Text.Encoding.UTF8, leaveOpen: true);

            var nodes = nodeMap.GetAllNodes();
            payloadWriter.Write(nodes.Count);

            if (nodes.Count == 0)
            {
                progress?.Report(1.0);
            }
            else
            {
                var written = 0;
                foreach (var kvp in nodes)
                {
                    WriteNode(payloadWriter, kvp.Key, kvp.Value);
                    written++;
                    progress?.Report((double)written / nodes.Count);
                }
            }

            payloadWriter.Flush();
        }
        finally
        {
            if (compress)
            {
                payloadStream.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads a version 1/2 image: capacity, label, node count, and nodes all live inside a
    /// single optionally gzip-compressed region right after the header — never encrypted.
    /// </summary>
    private static FileNodeMap LoadLegacy(
        FileStream stream,
        BinaryReader reader,
        int version,
        ImageCompressionLevel level,
        out ulong capacityBytes,
        out string volumeLabel)
    {
        var compressed = version == 2 && level != ImageCompressionLevel.None;

        using var payloadReader = compressed
            ? new BinaryReader(new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true), System.Text.Encoding.UTF8)
            : reader;

        capacityBytes = payloadReader.ReadUInt64();
        volumeLabel = payloadReader.ReadString();

        return ReadNodes(payloadReader);
    }

    /// <summary>
    /// Reads a version 3 or 4 image: capacity/label are always plaintext header fields; the node
    /// region (from node count onward) is compressed and, when encrypted, additionally wrapped in
    /// AES-256-GCM using the content-encryption key unwrapped from the password. Version 3 wraps
    /// the whole node region as one ciphertext blob (legacy, kept only for backward compatibility);
    /// version 4 uses independently encrypted chunks so no single buffer needs to hold the entire
    /// node region — see the class remarks and <see cref="ChunkedGcmReadStream"/>.
    /// </summary>
    private static FileNodeMap LoadCurrent(
        FileStream stream,
        BinaryReader reader,
        int version,
        ImageCompressionLevel level,
        bool isEncrypted,
        string? password,
        out ulong capacityBytes,
        out string volumeLabel,
        out byte[]? cek)
    {
        capacityBytes = reader.ReadUInt64();
        volumeLabel = reader.ReadString();
        cek = null;
        var compressed = level != ImageCompressionLevel.None;

        if (!isEncrypted)
        {
            // The node region is the last thing in the file for an unencrypted image, so
            // decompressing straight off the file stream (rather than buffering it) is safe —
            // GZipStream simply reads until end of file.
            return ReadNodeRegion(stream, compressed);
        }

        if (password is null)
        {
            throw new ImagePasswordRequiredException();
        }

        var salt = reader.ReadBytes(SaltSize);
        var iterations = reader.ReadInt32();
        var wrapNonce = reader.ReadBytes(NonceSize);
        var wrapTag = reader.ReadBytes(TagSize);
        var wrappedCek = reader.ReadBytes(CekSize);

        var resolvedCek = UnwrapCek(wrappedCek, password, salt, iterations, wrapNonce, wrapTag);
        cek = resolvedCek;

        return version == 3
            ? LoadLegacyEncryptedBlob(stream, reader, resolvedCek, compressed)
            : LoadChunkedEncrypted(stream, reader, resolvedCek, compressed);
    }

    /// <summary>
    /// Version 3's whole-blob encrypted node region: a single AES-256-GCM ciphertext covering the
    /// entire (already gzip-compressed) node region. Requires materializing the whole region as
    /// one byte array, which is what version 4 exists to avoid — kept only so pre-existing images
    /// keep loading.
    /// </summary>
    private static FileNodeMap LoadLegacyEncryptedBlob(
        FileStream stream,
        BinaryReader reader,
        byte[] cek,
        bool compressed)
    {
        var dataNonce = reader.ReadBytes(NonceSize);
        var dataTag = reader.ReadBytes(TagSize);
        var ciphertext = reader.ReadBytes((int)(stream.Length - stream.Position));

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aesGcm = new AesGcm(cek, TagSize);
            aesGcm.Decrypt(dataNonce, ciphertext, dataTag, plaintext);
        }
        catch (CryptographicException)
        {
            throw new ImagePasswordIncorrectException();
        }

        try
        {
            using var nodeRegionStream = new MemoryStream(plaintext, writable: false);
            return ReadNodeRegion(nodeRegionStream, compressed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Version 4's chunked encrypted node region: each chunk was independently AES-256-GCM
    /// encrypted on save, so decryption streams chunk-by-chunk via <see cref="ChunkedGcmReadStream"/>
    /// rather than requiring the whole region in memory at once.
    /// </summary>
    private static FileNodeMap LoadChunkedEncrypted(
        FileStream stream,
        BinaryReader reader,
        byte[] cek,
        bool compressed)
    {
        var baseNonce = reader.ReadBytes(NonceSize);

        try
        {
            using var chunkedStream = new ChunkedGcmReadStream(stream, cek, baseNonce);
            return ReadNodeRegion(chunkedStream, compressed);
        }
        catch (CryptographicException)
        {
            throw new ImagePasswordIncorrectException();
        }
    }

    /// <summary>
    /// Reads the node-count-prefixed node region from <paramref name="source"/>, transparently
    /// gzip-decompressing when <paramref name="compressed"/> is set. Mirrors <see cref="WriteNodeRegion"/>.
    /// </summary>
    private static FileNodeMap ReadNodeRegion(Stream source, bool compressed)
    {
        using var payloadReader = new BinaryReader(
            compressed
                ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true)
                : source,
            System.Text.Encoding.UTF8,
            leaveOpen: true);

        return ReadNodes(payloadReader);
    }

    private static void ReadHeader(
        BinaryReader reader,
        out int version,
        out ImageCompressionLevel level,
        out bool isEncrypted)
    {
        var magic = reader.ReadBytes(4);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a valid ManagedDrive image file.");
        }

        version = reader.ReadInt32();
        if (version is not (1 or 2 or 3 or 4))
        {
            throw new InvalidDataException($"Unsupported image version: {version}.");
        }

        level = version >= 2 ? (ImageCompressionLevel)reader.ReadByte() : ImageCompressionLevel.None;
        isEncrypted = version >= 3 && reader.ReadByte() != 0;
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
            var aligned = FileNode.AlignToAllocationUnit(node.FileInfo.AllocationSize);
            node.FileData = FileContent.CreateZeroed(aligned);
            node.FileData.FillFromStream(reader.BaseStream, dataLen);
        }
        else if (dataLen > 0)
        {
            // Skip data bytes for directories (should not occur in well-formed images)
            reader.ReadBytes((int)dataLen);
        }

        return (path, node);
    }

    private static FileNodeMap ReadNodes(BinaryReader payloadReader)
    {
        var nodeMap = new FileNodeMap();
        var count = payloadReader.ReadInt32();

        for (var i = 0; i < count; i++)
        {
            var (path, node) = ReadNode(payloadReader);
            nodeMap.Add(path, node);
        }

        return nodeMap;
    }

    private static CompressionLevel ToCompressionLevel(ImageCompressionLevel level) => level switch
    {
        ImageCompressionLevel.Fastest => CompressionLevel.Fastest,
        ImageCompressionLevel.SmallestSize => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };

    private static byte[] UnwrapCek(
        byte[] wrappedCek,
        string password,
        byte[] salt,
        int iterations,
        byte[] nonce,
        byte[] tag)
    {
        var kek = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, CekSize);
        try
        {
            var cek = new byte[wrappedCek.Length];
            try
            {
                using var aesGcm = new AesGcm(kek, TagSize);
                aesGcm.Decrypt(nonce, wrappedCek, tag, cek);
            }
            catch (CryptographicException)
            {
                throw new ImagePasswordIncorrectException();
            }

            return cek;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static byte[] WrapCek(
        byte[] cek,
        string password,
        byte[] salt,
        int iterations,
        out byte[] nonce,
        out byte[] tag)
    {
        var kek = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, CekSize);
        try
        {
            nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var wrapped = new byte[cek.Length];
            var localTag = new byte[TagSize];
            using (var aesGcm = new AesGcm(kek, TagSize))
            {
                aesGcm.Encrypt(nonce, cek, wrapped, localTag);
            }

            tag = localTag;
            return wrapped;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
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

        var security = node.FileSecurity ?? [];
        writer.Write(security.Length);
        writer.Write(security);

        if (node is { IsDirectory: false, FileData: not null, FileInfo.FileSize: > 0 })
        {
            var fileSize = Math.Min(node.FileInfo.FileSize, (ulong)node.FileData.Length);
            writer.Write((long)fileSize);
            node.FileData.CopyTo(writer.BaseStream, (long)fileSize);
        }
        else
        {
            writer.Write(0L);
        }
    }

    /// <summary>
    /// Derives a unique nonce for chunk <paramref name="chunkIndex"/> from a random per-save
    /// <paramref name="baseNonce"/> by XOR-ing its last 4 bytes with the big-endian chunk index.
    /// This is a standard segmented-AEAD nonce derivation: as long as <paramref name="baseNonce"/>
    /// is freshly random per save and chunk indices are never reused within that save (both true
    /// here — <see cref="ChunkedGcmWriteStream"/> increments a private counter once per chunk),
    /// every chunk gets a distinct nonce under the same key, which is AES-GCM's only requirement.
    /// </summary>
    private static byte[] DeriveChunkNonce(byte[] baseNonce, int chunkIndex)
    {
        var nonce = (byte[])baseNonce.Clone();
        Span<byte> indexBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(indexBytes, (uint)chunkIndex);

        for (var i = 0; i < indexBytes.Length; i++)
        {
            nonce[NonceSize - indexBytes.Length + i] ^= indexBytes[i];
        }

        return nonce;
    }

    /// <summary>
    /// Write-only <see cref="Stream"/> that buffers up to <see cref="DefaultChunkSize"/> (or the
    /// test override) bytes at a time and, on each full buffer plus once more on <see cref="Complete"/>,
    /// AES-256-GCM-encrypts that chunk with a nonce derived via <see cref="DeriveChunkNonce"/> and
    /// writes it to the underlying stream as <c>[Int32 ciphertext length][16-byte tag][ciphertext]</c>.
    /// This lets <see cref="Save"/> encrypt node regions of any size without ever holding the whole
    /// region (or a single AES-GCM call's input) in one buffer.
    /// </summary>
    private sealed class ChunkedGcmWriteStream(Stream output, byte[] key, byte[] baseNonce, int chunkSize) : Stream
    {
        private readonly byte[] _buffer = new byte[chunkSize];
        private int _bufferLength;
        private int _chunkIndex;
        private bool _completed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var toCopy = Math.Min(count, _buffer.Length - _bufferLength);
                Array.Copy(buffer, offset, _buffer, _bufferLength, toCopy);
                _bufferLength += toCopy;
                offset += toCopy;
                count -= toCopy;

                if (_bufferLength == _buffer.Length)
                {
                    FlushChunk();
                }
            }
        }

        public override void Flush()
        {
        }

        /// <summary>
        /// Flushes any partially filled chunk, then writes a final zero-length chunk as an
        /// explicit end-of-stream marker so the reader knows not to expect another chunk header.
        /// Must be called exactly once after all plaintext has been written, before disposing.
        /// </summary>
        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            if (_bufferLength > 0)
            {
                FlushChunk();
            }

            WriteChunk(ReadOnlySpan<byte>.Empty);
            _completed = true;
        }

        private void FlushChunk()
        {
            WriteChunk(_buffer.AsSpan(0, _bufferLength));
            CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, _bufferLength));
            _bufferLength = 0;
        }

        private void WriteChunk(ReadOnlySpan<byte> plaintext)
        {
            var nonce = DeriveChunkNonce(baseNonce, _chunkIndex);
            var ciphertext = plaintext.Length == 0 ? [] : new byte[plaintext.Length];
            var tag = new byte[TagSize];

            using (var aesGcm = new AesGcm(key, TagSize))
            {
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            Span<byte> lengthBytes = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, ciphertext.Length);
            output.Write(lengthBytes);
            output.Write(tag);
            if (ciphertext.Length > 0)
            {
                output.Write(ciphertext);
            }

            _chunkIndex++;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Read-only <see cref="Stream"/> counterpart to <see cref="ChunkedGcmWriteStream"/>: reads
    /// the <c>[length][tag][ciphertext]</c> chunk sequence from the underlying stream, decrypting
    /// each chunk with the matching derived nonce and exposing the concatenated plaintext as a
    /// normal readable stream (typically wrapped by a decompressing <see cref="GZipStream"/>).
    /// Throws <see cref="CryptographicException"/> (translated by the caller into
    /// <see cref="ImagePasswordIncorrectException"/>) if any chunk's tag fails to authenticate.
    /// </summary>
    private sealed class ChunkedGcmReadStream(Stream source, byte[] key, byte[] baseNonce) : Stream
    {
        private byte[] _currentChunk = [];
        private int _currentChunkLength;
        private int _positionInChunk;
        private int _chunkIndex;
        private bool _endOfStream;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var totalRead = 0;

            while (count > 0)
            {
                if (_positionInChunk == _currentChunkLength)
                {
                    if (_endOfStream || !TryReadNextChunk())
                    {
                        break;
                    }
                }

                var toCopy = Math.Min(count, _currentChunkLength - _positionInChunk);
                Array.Copy(_currentChunk, _positionInChunk, buffer, offset, toCopy);
                _positionInChunk += toCopy;
                offset += toCopy;
                count -= toCopy;
                totalRead += toCopy;
            }

            return totalRead;
        }

        private bool TryReadNextChunk()
        {
            Span<byte> lengthBytes = stackalloc byte[4];
            source.ReadExactly(lengthBytes);
            var ciphertextLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);

            var tag = new byte[TagSize];
            source.ReadExactly(tag);

            var ciphertext = ciphertextLength == 0 ? [] : new byte[ciphertextLength];
            if (ciphertextLength > 0)
            {
                source.ReadExactly(ciphertext);
            }

            var nonce = DeriveChunkNonce(baseNonce, _chunkIndex);
            var plaintext = ciphertextLength == 0 ? [] : new byte[ciphertextLength];
            using (var aesGcm = new AesGcm(key, TagSize))
            {
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            _chunkIndex++;

            if (ciphertextLength == 0)
            {
                _endOfStream = true;
                return false;
            }

            _currentChunk = plaintext;
            _currentChunkLength = plaintext.Length;
            _positionInChunk = 0;
            return true;
        }

        public override void Flush() => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}