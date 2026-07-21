using System.IO.Compression;
using System.Security.Cryptography;

namespace ManagedDrive.Core;

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
///   <item>Int32 version (currently 3)</item>
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
///   <item>When encrypted: data nonce (12), data tag (16) for the node region below.</item>
///   <item>The node region, gzip-compressed whenever the level is not <see cref="ImageCompressionLevel.None"/>, then AES-256-GCM encrypted with the CEK when the image is encrypted:</item>
///   <item>Int32 node count</item>
///   <item>For each node: path, metadata, security descriptor bytes, file data bytes</item>
/// </list>
/// </remarks>
public static class DiskImageSerializer
{
    private const int CekSize = 32;
    private const int NonceSize = 12;
    private const int Pbkdf2Iterations = 210_000;
    private const int SaltSize = 16;
    private const int TagSize = 16;
    private const int Version = 3;
    private static readonly byte[] Magic = "MDRD"u8.ToArray();

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
            : LoadV3(stream, reader, level, isEncrypted, password, out capacityBytes, out volumeLabel, out cek);
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
            // Version 3 layout: capacity/label are always plaintext header fields.
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
    /// The subsequent gzip compression and AES-GCM encryption steps operate on the whole
    /// serialized buffer as a single unit and are not individually reported.
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

        // Serialize the node region into memory first: it needs to be a single contiguous byte
        // array to gzip-compress and then AES-GCM-encrypt as one unit (the node map is already
        // fully materialized in memory anyway, so this adds no meaningful overhead).
        byte[] nodeRegionBytes;
        using (var nodeRegionStream = new MemoryStream())
        {
            using (var payloadWriter = new BinaryWriter(
                compress ? new GZipStream(nodeRegionStream, ToCompressionLevel(level), leaveOpen: true) : nodeRegionStream,
                System.Text.Encoding.UTF8,
                leaveOpen: true))
            {
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

            nodeRegionBytes = nodeRegionStream.ToArray();
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

                        var dataNonce = RandomNumberGenerator.GetBytes(NonceSize);
                        var ciphertext = new byte[nodeRegionBytes.Length];
                        var dataTag = new byte[TagSize];
                        using (var aesGcm = new AesGcm(enc.Cek, TagSize))
                        {
                            aesGcm.Encrypt(dataNonce, nodeRegionBytes, ciphertext, dataTag);
                        }

                        writer.Write(dataNonce);
                        writer.Write(dataTag);
                        writer.Write(ciphertext);
                    }
                    else
                    {
                        writer.Write(nodeRegionBytes);
                    }

                    writer.Flush();
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
    /// Reads a version 3 image: capacity/label are always plaintext header fields; the node
    /// region (from node count onward) is compressed and, when encrypted, additionally wrapped
    /// in AES-256-GCM using the content-encryption key unwrapped from the password.
    /// </summary>
    private static FileNodeMap LoadV3(
        FileStream stream,
        BinaryReader reader,
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

        byte[] nodeRegionBytes;

        if (isEncrypted)
        {
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

            var dataNonce = reader.ReadBytes(NonceSize);
            var dataTag = reader.ReadBytes(TagSize);
            var ciphertext = reader.ReadBytes((int)(stream.Length - stream.Position));

            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aesGcm = new AesGcm(resolvedCek, TagSize);
                aesGcm.Decrypt(dataNonce, ciphertext, dataTag, plaintext);
            }
            catch (CryptographicException)
            {
                throw new ImagePasswordIncorrectException();
            }

            nodeRegionBytes = plaintext;
        }
        else
        {
            nodeRegionBytes = reader.ReadBytes((int)(stream.Length - stream.Position));
        }

        var compressed = level != ImageCompressionLevel.None;
        using var nodeRegionStream = new MemoryStream(nodeRegionBytes);
        using var payloadReader = new BinaryReader(
            compressed
                ? new GZipStream(nodeRegionStream, CompressionMode.Decompress, leaveOpen: true)
                : nodeRegionStream,
            System.Text.Encoding.UTF8);

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
        if (version is not (1 or 2 or 3))
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

    private static byte[] WrapCek(
        byte[] cek,
        string password,
        byte[] salt,
        int iterations,
        out byte[] nonce,
        out byte[] tag)
    {
        var kek = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, CekSize);
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
            writer.Write(node.FileData, 0, (int)fileSize);
        }
        else
        {
            writer.Write(0L);
        }
    }
}