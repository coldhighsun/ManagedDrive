using System.IO.Compression;
using System.Runtime.ExceptionServices;
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
///   <item>Int32 version (currently 5)</item>
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
///     Version 4+ when encrypted: a random 12-byte base nonce, then a sequence of
///     chunks, each independently AES-256-GCM encrypted so no single buffer needs to hold the
///     whole node region. Each chunk is [Int32 ciphertext length][16-byte tag][ciphertext
///     bytes], terminated by a zero-length chunk. Per-chunk nonces are derived from the base
///     nonce by XOR-ing its last 4 bytes with the big-endian chunk index, guaranteeing a unique
///     nonce per chunk under the same key/base nonce (see <see cref="ChunkedGcm.DeriveChunkNonce"/>).
///     Identical layout for both version 4 and 5 — only the compression algorithm wrapped inside
///     differs.
///   </item>
///   <item>When not encrypted (any version): the node region follows directly, compressed whenever the level is not <see cref="ImageCompressionLevel.None"/>, streamed straight from/to the file rather than buffered.</item>
///   <item>
///     Compression algorithm is determined entirely by the file's version, not by any separate
///     field: versions 1-4 use gzip/deflate (<see cref="GZipStream"/>), read-only — nothing writes
///     gzip anymore. Version 5 (current) uses Zstd (<see cref="ZstdSharp"/>) for both writing and
///     reading, which compresses substantially faster than gzip at a comparable ratio.
///   </item>
///   <item>
///     Version 5's Zstd-compressed node region (whether encrypted or not) is itself a sequence of
///     independently compressed chunks framed as [Int32 compressed length][compressed bytes],
///     terminated by a zero-length chunk — mirroring <see cref="ChunkedGcm"/>'s chunk framing. This
///     lets both <see cref="ParallelZstd.WriteStream"/> and <see cref="ParallelZstd.ReadStream"/>
///     compress/decompress chunks concurrently across a worker pool while still writing/reading
///     them in original order, since chunk boundaries are known up front rather than requiring a
///     full decompress pass to discover. Encrypted images wrap this chunk sequence in
///     <see cref="ChunkedGcm"/>'s own (independently sized) chunking, so the two chunk boundaries
///     do not line up — that's fine, since Zstd's chunk framing is entirely self-describing.
///   </item>
///   <item>Node region contents: Int32 node count, then for each node: path, metadata, security descriptor bytes, file data bytes</item>
///   <item>
///     Version 6 (segmented node region; written by <see cref="SaveIncremental"/>, which is what
///     <see cref="RamDisk.SaveToImage"/> uses for every production auto-save and manual save):
///     replaces the single continuous node region with a sequence of independently compressed and
///     (when encrypted) independently encrypted <em>segments</em>, each covering a contiguous run
///     of nodes. <see cref="SaveIncremental"/> reuses the on-disk bytes of a segment verbatim,
///     without recompressing or re-encrypting it, whenever every node in that segment is unchanged
///     since the prior save (tracked per-node via <see cref="FileNode.SavedContentVersion"/>,
///     <see cref="FileNode.SavedMetadataVersion"/>, and <see cref="FileNode.SavedSegmentIndex"/>),
///     falling back to a full rewrite when there is no compatible existing image to reuse (first
///     save, or the existing image predates version 6). Layout after the plaintext capacity/label
///     (and, when encrypted, the same key-wrap fields as version
///     3+ — but no single file-level base nonce, since each segment carries its own nonce):
///     <list type="bullet">
///       <item>Int32 segment count</item>
///       <item>
///         Per segment: Int32 node count, Int64 payload length, 32-byte SHA-256 hash of the
///         segment's uncompressed plaintext (for future incremental-save change detection and
///         integrity checking), and — only when encrypted — a 12-byte nonce and 16-byte GCM tag
///         for that segment.
///       </item>
///       <item>
///         Segment payloads follow back to back, each exactly its indexed payload length: when
///         encrypted, one AES-256-GCM ciphertext per segment, encrypted with the single-shot
///         <see cref="AesGcm"/> API and so, like version 3's whole-image blob, capped near 2 GB —
///         <see cref="SaveIncremental"/> avoids ever hitting that cap by diverting the whole save to
///         the non-segmented <see cref="Save"/> instead whenever a single node's content approaches
///         it (see <see cref="MaxSafeSegmentNodeBytes"/>); the plaintext of each (post-decryption, if
///         encrypted) is itself Zstd-compressed using the same <see cref="ParallelZstd"/> chunk
///         framing as version 5 when compression is enabled, or the raw node bytes when it is not.
///       </item>
///     </list>
///   </item>
/// </list>
/// </remarks>
public static class DiskImageSerializer
{
    private const int CekSize = 32;

    /// <summary>
    /// Buffer size for the image file's <see cref="FileStream"/>. Save/Load are purely sequential,
    /// large-volume I/O, so a larger-than-default (4 KB) buffer cuts the number of read/write
    /// syscalls substantially — this matters most for uncompressed images, where node metadata is
    /// written directly to this stream in many small <see cref="BinaryWriter"/> calls rather than
    /// through a buffering <see cref="System.IO.Compression.GZipStream"/>.
    /// </summary>
    private const int FileStreamBufferSize = 1024 * 1024;

    private const int NonceSize = 12;
    private const int Pbkdf2Iterations = 210_000;
    private const int SaltSize = 16;
    private const int SegmentedVersion = 6;

    /// <summary>
    /// Target uncompressed byte size per segment in a version 6 image, based on summed
    /// <see cref="Fsp.Interop.FileInfo.AllocationSize"/> of the nodes placed into it. A segment
    /// always gets at least one node even if that single node's allocation size alone exceeds this
    /// target, so segment size has no hard upper bound. Chosen as a middle ground: small enough
    /// that a typical save touching a handful of files only has to rewrite a handful of segments,
    /// large enough that Zstd compression ratio and the fixed per-segment overhead (GCM tag, hash,
    /// index entry) don't dominate.
    /// </summary>
    private const long SegmentTargetBytes = 4L * 1024 * 1024;

    /// <summary>
    /// A segment always holds at least one whole node's content (see <see cref="SegmentTargetBytes"/>),
    /// so a single node whose own content approaches this size can't safely be built by
    /// <see cref="BuildSegmentPayload"/>, which buffers a segment's plaintext in one
    /// <see cref="MemoryStream"/> and (when encrypted) encrypts it with <see cref="AesGcm"/>'s
    /// single-shot API — both capped near 2 GB. <see cref="SaveIncremental"/> checks node content
    /// against this (deliberately conservative, well under that ceiling) threshold and diverts the
    /// whole save to the non-segmented <see cref="Save"/>, which streams without any such limit,
    /// rather than let the segmented writer overflow.
    /// </summary>
    private const ulong MaxSafeSegmentNodeBytes = 1UL * 1024 * 1024 * 1024;

    /// <summary>
    /// Rough per-node size of the fixed metadata fields <see cref="WriteNode"/> emits, excluding
    /// the path. Only used to pre-size a segment's plaintext buffer.
    /// </summary>
    private const int EstimatedNodeMetadataBytes = 128;

    private const int Sha256Size = 32;
    private const int TagSize = 16;
    private const int Version = 5;
    private static readonly byte[] Magic = "MDRD"u8.ToArray();

    /// <summary>
    /// Logger for recoverable anomalies found in an existing image while saving over it.
    /// </summary>
    private static readonly ILogger Logger = AppLog.CreateLogger(typeof(DiskImageSerializer));

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
    /// <param name="progress">
    /// Optional progress reporter, updated with the fraction of the image file's raw bytes read
    /// so far (<c>stream.Position / stream.Length</c>), each time a node finishes loading. This is
    /// a proxy for actual node-content progress (the file may be compressed/encrypted), but it is
    /// monotonic and reflects real I/O progress. The total decompressed content size isn't known
    /// up front (no such field in the header), so this is the best available signal.
    /// </param>
    public static FileNodeMap Load(
        string imagePath,
        out ulong capacityBytes,
        out string volumeLabel,
        string? password,
        out byte[]? cek,
        IProgress<double>? progress = null)
    {
        using var stream = new FileStream(imagePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            BufferSize = FileStreamBufferSize,
            Options = FileOptions.SequentialScan,
        });

        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);

        ReadHeader(reader, out var version, out var level, out var isEncrypted);
        cek = null;

        Action? reportTick = progress is null
            ? null
            : () => progress.Report(stream.Length > 0 ? (double)stream.Position / stream.Length : 1.0);

        return version <= 2
            ? LoadLegacy(stream, reader, version, level, out capacityBytes, out volumeLabel, reportTick)
            : LoadCurrent(stream, reader, version, level, isEncrypted, password, out capacityBytes, out volumeLabel, out cek, reportTick);
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
        using var stream = new FileStream(imagePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            BufferSize = FileStreamBufferSize,
            Options = FileOptions.SequentialScan,
        });
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);

        ReadHeader(reader, out var version, out var level, out isEncrypted);

        if (version <= 2)
        {
            // Legacy layout: capacity/label are inside the optionally compressed payload.
            using var payloadReader = OpenLegacyPayloadReader(stream, reader, version, level);
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
    /// Optional progress reporter, updated with a fraction in [0, 1] as each node is written,
    /// weighted by each node's allocation size against <see cref="FileNodeMap.GetTotalAllocated"/>
    /// (falls back to an even per-node fraction when the total is zero, e.g. all-empty-directories).
    /// The subsequent gzip compression and (when encrypting) AES-256-GCM chunk encryption happen
    /// as nodes stream through and are not individually reported.
    /// </param>
    /// <param name="customZstdLevel">
    /// Optional advanced override (1-22) of the exact Zstd level used instead of the one mapped
    /// from <paramref name="level"/> (see <see cref="ImageCompressionLevelExtensions.ToZstdLevel"/>).
    /// Purely an encoder-side speed/ratio choice — not persisted in the image itself, since Zstd
    /// decompression needs no level parameter, so <see cref="Load"/> works identically regardless
    /// of what level a given image was originally written with.
    /// </param>
    public static void Save(
        FileNodeMap nodeMap,
        ulong capacityBytes,
        string volumeLabel,
        string imagePath,
        ImageCompressionLevel level,
        ImageEncryptionInfo? encryption = null,
        IProgress<double>? progress = null,
        int? customZstdLevel = null)
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
            using (var stream = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                BufferSize = FileStreamBufferSize,
                Options = FileOptions.SequentialScan,
            }))
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
                        using var chunkedStream = new ChunkedGcm.WriteStream(stream, enc.Cek, baseNonce, ChunkedGcm.ChunkSize);
                        WriteNodeRegion(chunkedStream, compress, level, customZstdLevel, nodeMap, progress);
                        chunkedStream.Complete();
                    }
                    else
                    {
                        writer.Flush();
                        WriteNodeRegion(stream, compress, level, customZstdLevel, nodeMap, progress);
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
    /// Writes the version 6 segmented image format (see the class remarks) to
    /// <paramref name="imagePath"/>, reusing the segments of any existing image already there that
    /// are still fully clean instead of recompressing/re-encrypting the whole disk. When
    /// <paramref name="imagePath"/> already holds a version 6 image written with the same
    /// compression level and encryption state, segments whose member nodes are all unchanged since
    /// that save (see <see cref="FileNode.SavedContentVersion"/>/<see cref="FileNode.SavedMetadataVersion"/>)
    /// are copied verbatim — no recompression, no re-encryption — and only segments containing new,
    /// modified, or removed nodes are rewritten. Otherwise (first save, prior image missing or not
    /// version 6, or its compression/encryption settings differ) this falls back to a full
    /// segmented rewrite, which then becomes the base a later call can build on.
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
    /// <param name="progress">Optional progress reporter, updated with a fraction in [0, 1].</param>
    /// <param name="customZstdLevel">
    /// Optional advanced override (1-22) of the exact Zstd level used instead of the one mapped
    /// from <paramref name="level"/> (see <see cref="ImageCompressionLevelExtensions.ToZstdLevel"/>).
    /// </param>
    /// <param name="forceFullRewrite">
    /// When <see langword="true"/>, skips segment reuse entirely and performs a full
    /// <see cref="SaveSegmented"/> rewrite, as if no existing image were present. Callers must set
    /// this after rotating the content-encryption key (e.g. removing and re-adding a password)
    /// while an existing image on disk may still be encrypted under the previous key — segment
    /// reuse only compares the encrypted/unencrypted flag, not key identity, so reusing that
    /// image's segments verbatim under a new key would leave the image undecryptable even with the
    /// correct new password.
    /// </param>
    public static void SaveIncremental(
        FileNodeMap nodeMap,
        ulong capacityBytes,
        string volumeLabel,
        string imagePath,
        ImageCompressionLevel level,
        ImageEncryptionInfo? encryption = null,
        IProgress<double>? progress = null,
        int? customZstdLevel = null,
        bool forceFullRewrite = false)
    {
        if (nodeMap.GetAllNodes().Any(kvp => kvp.Value.FileInfo.AllocationSize > MaxSafeSegmentNodeBytes))
        {
            Save(nodeMap, capacityBytes, volumeLabel, imagePath, level, encryption, progress, customZstdLevel);
            return;
        }

        if (forceFullRewrite)
        {
            SaveSegmented(nodeMap, capacityBytes, volumeLabel, imagePath, level, encryption, progress,
                customZstdLevel, SegmentTargetBytes);
            return;
        }

        SaveSegmentedIncremental(nodeMap, capacityBytes, volumeLabel, imagePath, level, encryption, progress,
            customZstdLevel, SegmentTargetBytes);
    }

    /// <summary>
    /// Test-only entry point into <see cref="SaveSegmented"/> that exposes
    /// <paramref name="segmentTargetBytes"/> directly, so tests can force small test fixtures to
    /// split across multiple segments without needing megabytes of node content. Production
    /// callers always go through <see cref="SaveIncremental"/>, which uses the fixed
    /// <see cref="SegmentTargetBytes"/>.
    /// </summary>
    internal static void SaveSegmentedForTest(
        FileNodeMap nodeMap,
        ulong capacityBytes,
        string volumeLabel,
        string imagePath,
        ImageCompressionLevel level,
        ImageEncryptionInfo? encryption,
        long segmentTargetBytes)
    {
        SaveSegmented(nodeMap, capacityBytes, volumeLabel, imagePath, level, encryption, progress: null,
            customZstdLevel: null, segmentTargetBytes);
    }

    /// <summary>
    /// Test-only entry point into <see cref="SaveSegmentedIncremental"/> that exposes
    /// <paramref name="segmentTargetBytes"/> directly, mirroring <see cref="SaveSegmentedForTest"/>.
    /// </summary>
    internal static void SaveSegmentedIncrementalForTest(
        FileNodeMap nodeMap,
        ulong capacityBytes,
        string volumeLabel,
        string imagePath,
        ImageCompressionLevel level,
        ImageEncryptionInfo? encryption,
        long segmentTargetBytes)
    {
        SaveSegmentedIncremental(nodeMap, capacityBytes, volumeLabel, imagePath, level, encryption, progress: null,
            customZstdLevel: null, segmentTargetBytes);
    }

    /// <summary>
    /// Serializes <paramref name="chunkNodes"/> into a single version 6 segment payload: writes
    /// each node, hashes the resulting plaintext, Zstd-compresses it (unless
    /// <paramref name="level"/> is <see cref="ImageCompressionLevel.None"/>), and — when
    /// <paramref name="encryption"/> is supplied — AES-256-GCM encrypts it under a freshly
    /// generated nonce. Shared by <see cref="SaveSegmented"/>'s per-segment loop and
    /// <see cref="SaveSegmentedIncremental"/>'s rewrite-pool loop so the two write paths can't
    /// silently drift apart. <paramref name="onNodeWritten"/>, if given, is invoked once per node
    /// right after it's written, for callers that track written-byte progress.
    /// </summary>
    private static (byte[] Payload, byte[] ContentHash, byte[]? Nonce, byte[]? Tag) BuildSegmentPayload(
        IEnumerable<KeyValuePair<string, FileNode>> chunkNodes,
        ImageCompressionLevel level,
        int? customZstdLevel,
        ImageEncryptionInfo? encryption,
        Action<FileNode>? onNodeWritten = null)
    {
        // Pre-size the plaintext buffer so it isn't grown by repeated doubling (each step a fresh
        // large-object-heap array plus a copy). Only an estimate: sizes may change concurrently
        // and the per-node metadata overhead is approximate, so the stream still grows if needed.
        long estimatedBytes = 0;
        foreach (var kvp in chunkNodes)
        {
            estimatedBytes += (long)kvp.Value.FileInfo.FileSize + EstimatedNodeMetadataBytes + kvp.Key.Length * 3;
        }

        using var plainStream = new MemoryStream((int)Math.Min(estimatedBytes, Array.MaxLength));
        using (var plainWriter = new BinaryWriter(plainStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (var kvp in chunkNodes)
            {
                WriteNode(plainWriter, kvp.Key, kvp.Value);
                onNodeWritten?.Invoke(kvp.Value);
            }

            plainWriter.Flush();
        }

        // Work on the stream's own buffer instead of a ToArray() copy of it; everything past
        // plainLength is unused capacity.
        var plainBuffer = plainStream.GetBuffer();
        var plainLength = (int)plainStream.Length;
        var contentHash = SHA256.HashData(plainBuffer.AsSpan(0, plainLength));

        // The payload is retained until the whole image is written, so it must be exactly sized.
        // Compression already produces an exact-size array; uncompressed output needs a trimmed
        // copy of the plaintext buffer (unless it happens to be exactly full already).
        var compressed = level != ImageCompressionLevel.None;
        byte[] payload;
        if (compressed)
        {
            payload = ParallelZstd.CompressFramed(plainBuffer, plainLength, level.ToZstdLevel(customZstdLevel));
        }
        else if (encryption is null && plainBuffer.Length != plainLength)
        {
            payload = plainBuffer.AsSpan(0, plainLength).ToArray();
        }
        else
        {
            payload = plainBuffer;
        }

        if (encryption is not { } enc)
        {
            return (payload, contentHash, null, null);
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        using var aesGcm = new AesGcm(enc.Cek, TagSize);
        if (compressed)
        {
            // The compressed payload is a private, exactly-sized array: encrypt it in place.
            aesGcm.Encrypt(nonce, payload, payload, tag);
            return (payload, contentHash, nonce, tag);
        }

        // Uncompressed: encrypting into a fresh exact-size array doubles as the trim.
        var ciphertext = new byte[plainLength];
        aesGcm.Encrypt(nonce, plainBuffer.AsSpan(0, plainLength), ciphertext, tag);
        return (ciphertext, contentHash, nonce, tag);
    }

    /// <summary>
    /// Reports <paramref name="fraction"/> through <paramref name="progress"/> only if it's
    /// strictly greater than the last value reported, so concurrent callers racing on a shared
    /// <see cref="Parallel.For(int,int,Action{int})"/> loop can't make the reported progress jump
    /// backward relative to what was reported before it.
    /// </summary>
    private static void ReportMonotonic(IProgress<double>? progress, double fraction, Lock progressLock, ref double lastReportedFraction)
    {
        if (progress is null)
        {
            return;
        }

        lock (progressLock)
        {
            if (fraction > lastReportedFraction)
            {
                lastReportedFraction = fraction;
                progress.Report(fraction);
            }
        }
    }

    /// <summary>
    /// Unwraps an <see cref="AggregateException"/> thrown out of a <see cref="Parallel.For(int,int,Action{int})"/>
    /// segment-compression loop back down to the single original exception a caller of the old,
    /// sequential per-segment loop would have seen, preserving its original stack trace.
    /// Cancellation is raised through progress reports, so several workers typically observe it at
    /// once; their <see cref="OperationCanceledException"/>s are collapsed into one (or dropped in
    /// favor of a single genuine failure alongside them), so callers that treat cancellation
    /// differently from failure still see a plain cancellation rather than an aggregate.
    /// </summary>
    internal static Exception Unwrap(AggregateException ex)
    {
        var inner = ex.Flatten().InnerExceptions;
        var failures = inner.Where(e => e is not OperationCanceledException).ToList();
        if (failures.Count == 0)
        {
            ExceptionDispatchInfo.Capture(inner[0]).Throw();
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        return new AggregateException(failures);
    }

    /// <summary>
    /// Version 4/5's chunked encrypted node region: each chunk was independently AES-256-GCM
    /// encrypted on save, so decryption streams chunk-by-chunk via <see cref="ChunkedGcm.ReadStream"/>
    /// rather than requiring the whole region in memory at once. <paramref name="useZstd"/>
    /// distinguishes the compression algorithm wrapped inside (version 4 = gzip, version 5 = Zstd).
    /// </summary>
    private static FileNodeMap LoadChunkedEncrypted(
        FileStream stream,
        BinaryReader reader,
        byte[] cek,
        bool compressed,
        bool useZstd,
        Action? reportTick = null)
    {
        var baseNonce = reader.ReadBytes(NonceSize);

        try
        {
            using var chunkedStream = new ChunkedGcm.ReadStream(stream, cek, baseNonce);
            return ReadNodeRegion(chunkedStream, compressed, useZstd, reportTick);
        }
        catch (CryptographicException)
        {
            throw new ImagePasswordIncorrectException();
        }
    }

    /// <summary>
    /// Reads a version 3 or 4 image: capacity/label are always plaintext header fields; the node
    /// region (from node count onward) is compressed and, when encrypted, additionally wrapped in
    /// AES-256-GCM using the content-encryption key unwrapped from the password. Version 3 wraps
    /// the whole node region as one ciphertext blob (legacy, kept only for backward compatibility);
    /// version 4 uses independently encrypted chunks so no single buffer needs to hold the entire
    /// node region — see the class remarks and <see cref="ChunkedGcm.ReadStream"/>.
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
        out byte[]? cek,
        Action? reportTick = null)
    {
        capacityBytes = reader.ReadUInt64();
        volumeLabel = reader.ReadString();
        cek = null;
        var compressed = level != ImageCompressionLevel.None;

        if (version == SegmentedVersion)
        {
            return LoadSegmented(reader, compressed, isEncrypted, password, out cek, reportTick);
        }

        var useZstd = version >= 5;

        if (!isEncrypted)
        {
            // The node region is the last thing in the file for an unencrypted image, so
            // decompressing straight off the file stream (rather than buffering it) is safe —
            // the decompression stream simply reads until end of file.
            return ReadNodeRegion(stream, compressed, useZstd, reportTick);
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

        return version switch
        {
            3 => LoadLegacyEncryptedBlob(stream, reader, resolvedCek, compressed, reportTick),
            4 or 5 => LoadChunkedEncrypted(stream, reader, resolvedCek, compressed, useZstd, reportTick),
            _ => throw new InvalidDataException($"Unsupported image version: {version}."),
        };
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
        out string volumeLabel,
        Action? reportTick = null)
    {
        using var payloadReader = OpenLegacyPayloadReader(stream, reader, version, level);
        capacityBytes = payloadReader.ReadUInt64();
        volumeLabel = payloadReader.ReadString();

        return ReadNodes(payloadReader, reportTick);
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
        bool compressed,
        Action? reportTick = null)
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
            // The whole ciphertext was already read off `stream` above, so `stream.Position` is
            // already at (or near) end-of-file here — reportTick will jump close to 1.0 on the
            // first node and stay there for the rest of this legacy (version 3) path.
            using var nodeRegionStream = new MemoryStream(plaintext, writable: false);
            return ReadNodeRegion(nodeRegionStream, compressed, useZstd: false, reportTick);
        }
        finally
        {
            SecureZero.All(plaintext);
        }
    }

    /// <summary>
    /// Reads a version 6 (segmented) image: after the plaintext capacity/label and, when
    /// encrypted, the same key-wrap fields as version 3+, reads the segment index and then each
    /// segment's payload in turn, decrypting (if encrypted), decompressing (if compressed) and
    /// parsing the segments concurrently, since each is independent, while adding their nodes to
    /// the map in file order. See the class remarks for the on-disk layout. Segment sizes are
    /// normally bounded by the writer to a few MB, so decrypting a segment's ciphertext with the
    /// single-shot <see cref="AesGcm"/> API here is safe in practice — <see cref="SaveIncremental"/>
    /// never writes a segment close to its ~2 GB ceiling, diverting to the non-segmented
    /// <see cref="Save"/> instead whenever a single node's content approaches it.
    /// </summary>
    private static FileNodeMap LoadSegmented(
        BinaryReader reader,
        bool compressed,
        bool isEncrypted,
        string? password,
        out byte[]? cek,
        Action? reportTick)
    {
        cek = null;
        byte[]? resolvedCek = null;

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

            resolvedCek = UnwrapCek(wrappedCek, password, salt, iterations, wrapNonce, wrapTag);
            cek = resolvedCek;
        }

        var segmentCount = reader.ReadInt32();
        var segments = new SegmentIndexEntry[segmentCount];
        for (var i = 0; i < segmentCount; i++)
        {
            var nodeCount = reader.ReadInt32();
            var payloadLength = reader.ReadInt64();
            var contentHash = reader.ReadBytes(Sha256Size);
            byte[]? nonce = null;
            byte[]? tag = null;
            if (isEncrypted)
            {
                nonce = reader.ReadBytes(NonceSize);
                tag = reader.ReadBytes(TagSize);
            }

            segments[i] = new(nodeCount, payloadLength, contentHash, nonce, tag);
        }

        // Segments are independent, so they're decoded (decrypted, decompressed and parsed) on the
        // thread pool, while this thread keeps reading payloads off the file in order and adds the
        // decoded nodes to the map in that same order. At most `window` segments are in flight,
        // bounding the extra memory to that many raw payloads rather than the whole image.
        var window = Math.Max(1, Environment.ProcessorCount);
        var pending = new Queue<Task<List<(string Path, FileNode Node)>>>();
        var nodeMap = new FileNodeMap();
        var nextSegmentToAdd = 0;

        void AddDecodedSegment(List<(string Path, FileNode Node)> nodes)
        {
            foreach (var (path, node) in nodes)
            {
                // Stamp the freshly-loaded node as already saved in this segment, so a subsequent
                // incremental save recognizes it as clean and reuses the segment verbatim instead
                // of treating every node as "never saved" (SavedSegmentIndex defaults to -1) and
                // rewriting the whole image on the very next save.
                node.SavedContentVersion = node.ContentVersion;
                node.SavedMetadataVersion = node.MetadataVersion;
                node.SavedSegmentIndex = nextSegmentToAdd;

                nodeMap.Add(path, node);
                reportTick?.Invoke();
            }

            nextSegmentToAdd++;
        }

        void DrainOne() => AddDecodedSegment(pending.Dequeue().GetAwaiter().GetResult());

        try
        {
            foreach (var segment in segments)
            {
                var payload = ReadSegmentPayload(reader, segment.PayloadLength);

                // A payload spanning several Zstd chunks (typically one large file on its own)
                // gains more from decompressing its own chunks in parallel than from a lone worker
                // thread, so it gets full parallelism instead of the parallelism=1 given to smaller
                // segments below. Its own chunk tasks (up to Environment.ProcessorCount of them)
                // would multiply with the window's worth of other in-flight segments otherwise, so
                // everything queued so far is drained first to keep at most one full-parallelism
                // decode in flight at a time — but it's still queued via Task.Run rather than run
                // inline here, so this thread keeps reading ahead instead of blocking on it (which
                // would tie up a pool thread while later work queues behind it).
                if (compressed && payload.Length >= ParallelZstd.ChunkSize)
                {
                    while (pending.Count > 0)
                    {
                        DrainOne();
                    }

                    pending.Enqueue(Task.Run(() => DecodeSegment(payload, segment, resolvedCek, compressed, zstdParallelism: null)));
                    continue;
                }

                if (pending.Count >= window)
                {
                    DrainOne();
                }

                pending.Enqueue(Task.Run(() => DecodeSegment(payload, segment, resolvedCek, compressed, zstdParallelism: 1)));
            }

            while (pending.Count > 0)
            {
                DrainOne();
            }
        }
        catch
        {
            // Let decodes already in flight finish before surfacing the failure, rather than
            // leaving them allocating content in the background for a load that has failed.
            foreach (var task in pending)
            {
                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch
                {
                    // The first failure is the one being reported.
                }
            }

            throw;
        }

        return nodeMap;
    }

    /// <summary>
    /// Decrypts (in place, when <paramref name="cek"/> is given), decompresses (when
    /// <paramref name="compressed"/>) and parses one version 6 segment's payload into its nodes,
    /// in file order. Safe to run concurrently for different segments.
    /// </summary>
    /// <param name="zstdParallelism">
    /// Degree of parallelism for decompressing the segment's Zstd chunks; <c>null</c> for the
    /// default (processor count).
    /// </param>
    private static List<(string Path, FileNode Node)> DecodeSegment(
        byte[] payload,
        SegmentIndexEntry segment,
        byte[]? cek,
        bool compressed,
        int? zstdParallelism)
    {
        if (cek is null)
        {
            return ParseNodes(payload, segment, compressed, zstdParallelism);
        }

        try
        {
            using var aesGcm = new AesGcm(cek, TagSize);
            aesGcm.Decrypt(segment.Nonce!, payload, segment.Tag!, payload);
        }
        catch (CryptographicException)
        {
            throw new ImagePasswordIncorrectException();
        }

        try
        {
            return ParseNodes(payload, segment, compressed, zstdParallelism);
        }
        finally
        {
            // payload now holds this segment's decrypted plaintext (compressed node bytes, or the
            // node bytes themselves) — zeroed once it's been fully read, the same reasoning as the
            // chunk-buffer zeroing in ChunkedGcm/ParallelZstd.
            SecureZero.All(payload);
        }
    }

    private static List<(string Path, FileNode Node)> ParseNodes(byte[] payload, SegmentIndexEntry segment, bool compressed, int? zstdParallelism)
    {
        using var payloadStream = new MemoryStream(payload, writable: false);
        using var nodeStream = compressed ? new ParallelZstd.ReadStream(payloadStream, zstdParallelism) : null;
        using var payloadReader = new BinaryReader(nodeStream ?? (Stream)payloadStream, System.Text.Encoding.UTF8, leaveOpen: true);

        // NodeCount comes from the file, so it only caps the initial capacity.
        var nodes = new List<(string Path, FileNode Node)>(Math.Clamp(segment.NodeCount, 0, 4096));
        for (var i = 0; i < segment.NodeCount; i++)
        {
            nodes.Add(ReadNode(payloadReader));
        }

        return nodes;
    }

    /// <summary>
    /// Opens the reader over a version 1/2 image's single payload region — gzip-decompressing it
    /// first when the image is a compressed version 2 (version 1 is never compressed). Shared by
    /// <see cref="PeekHeader"/> (reads capacity/label only) and <see cref="LoadLegacy"/> (reads
    /// capacity/label, then the full node region) so both stay in sync on this legacy layout rule.
    /// </summary>
    private static BinaryReader OpenLegacyPayloadReader(FileStream stream, BinaryReader reader, int version, ImageCompressionLevel level)
    {
        var compressed = version == 2 && level != ImageCompressionLevel.None;
        return compressed
            ? new(new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true), System.Text.Encoding.UTF8)
            : reader;
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
        if (version is not (1 or 2 or 3 or 4 or 5 or SegmentedVersion))
        {
            throw new InvalidDataException($"Unsupported image version: {version}.");
        }

        level = version >= 2 ? (ImageCompressionLevel)reader.ReadByte() : ImageCompressionLevel.None;
        isEncrypted = version >= 3 && reader.ReadByte() != 0;
    }

    private static (string Path, FileNode Node) ReadNode(BinaryReader reader)
    {
        var metadata = NodeMetadataIO.ReadMetadata(reader);
        var path = metadata.Path;

        var node = new FileNode
        {
            FileInfo = metadata.FileInfo,
            FileSecurity = metadata.Security,
        };

        var dataLen = reader.ReadInt64();
        if (dataLen > 0 && !node.IsDirectory)
        {
            // Older builds could save a file caught growing with an AllocationSize (and FileSize)
            // out of step with its data. Size the content to cover all of them rather than fail
            // the whole load, keeping FileSize <= AllocationSize == content length as reads assume.
            var aligned = FileNode.AlignToAllocationUnit(
                Math.Max(node.FileInfo.AllocationSize, Math.Max((ulong)dataLen, node.FileInfo.FileSize)));
            node.FileInfo.AllocationSize = aligned;
            node.FileData = FileContent.CreateZeroed(aligned);

            // FillFromStream zero-pads a short read instead of throwing; a truncated image must
            // fail the load, or the next save would persist the zero-padded file as if it were
            // the real content.
            var filled = node.FileData.FillFromStream(reader.BaseStream, dataLen);
            if (filled < dataLen)
            {
                throw new InvalidDataException(
                    $"Image is truncated: file '{path}' has {filled:N0} of {dataLen:N0} bytes.");
            }
        }
        else if (dataLen > 0)
        {
            // Skip data bytes for directories (should not occur in well-formed images)
            reader.ReadBytes((int)dataLen);
        }

        return (path, node);
    }

    /// <summary>
    /// Reads the node-count-prefixed node region from <paramref name="source"/>, transparently
    /// decompressing when <paramref name="compressed"/> is set — via Zstd when
    /// <paramref name="useZstd"/> is set (version 5, current), otherwise via gzip (versions 1-4,
    /// read-only). Mirrors <see cref="WriteNodeRegion"/>.
    /// </summary>
    private static FileNodeMap ReadNodeRegion(Stream source, bool compressed, bool useZstd, Action? reportTick = null)
    {
        // leaveOpen is false for the compressed branches so disposing payloadReader disposes the
        // locally-constructed decompressing wrapper too — it owns no other references, and
        // ParallelZstd.ReadStream now owns native decompressor state that must be released.
        // source itself is always left open: it's owned by the caller, not this method.
        using var payloadReader = compressed
            ? useZstd
                ? new BinaryReader(new ParallelZstd.ReadStream(source), System.Text.Encoding.UTF8, leaveOpen: false)
                : new BinaryReader(new GZipStream(source, CompressionMode.Decompress, leaveOpen: true), System.Text.Encoding.UTF8, leaveOpen: false)
            : new BinaryReader(source, System.Text.Encoding.UTF8, leaveOpen: true);

        return ReadNodes(payloadReader, reportTick);
    }

    private static FileNodeMap ReadNodes(BinaryReader payloadReader, Action? reportTick = null)
    {
        var nodeMap = new FileNodeMap();
        var count = payloadReader.ReadInt32();

        for (var i = 0; i < count; i++)
        {
            var (path, node) = ReadNode(payloadReader);
            nodeMap.Add(path, node);
            reportTick?.Invoke();
        }

        return nodeMap;
    }

    /// <summary>
    /// Writes a version 6 (segmented) image, grouping consecutive nodes (in the sorted order
    /// <see cref="FileNodeMap.GetAllNodes"/> returns them) into segments whose summed allocation
    /// size is close to <paramref name="segmentTargetBytes"/>. This full rewrite still
    /// recompresses/re-encrypts every node on every call — used both as the very first save that
    /// establishes each node's <see cref="FileNode.SavedSegmentIndex"/> baseline, and as
    /// <see cref="SaveSegmentedIncremental"/>'s fallback whenever there is no compatible existing
    /// image to reuse segments from.
    /// </summary>
    private static void SaveSegmented(
        FileNodeMap nodeMap,
        ulong capacityBytes,
        string volumeLabel,
        string imagePath,
        ImageCompressionLevel level,
        ImageEncryptionInfo? encryption,
        IProgress<double>? progress,
        int? customZstdLevel,
        long segmentTargetBytes)
    {
        var directory = Path.GetDirectoryName(imagePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var nodes = nodeMap.GetAllNodes();
        var totalBytes = nodeMap.GetTotalAllocated();

        // Captured per node before writing, so the post-save version bookkeeping below reflects
        // exactly the state that was actually persisted, not whatever a concurrent WinFsp write
        // might have bumped it to while this save was in flight.
        var capturedContentVersion = new ulong[nodes.Count];
        var capturedMetadataVersion = new ulong[nodes.Count];
        var nodeSegmentIndex = new int[nodes.Count];

        // Pass 1: decide segment boundaries by AllocationSize only (pure in-memory bookkeeping,
        // no compression yet) so segment count/order stays identical to the old sequential
        // behavior regardless of how pass 2 below is parallelized.
        var chunks = new List<List<KeyValuePair<string, FileNode>>>();
        var cursor = 0;

        while (cursor < nodes.Count)
        {
            var segmentIndex = chunks.Count;
            var chunk = new List<KeyValuePair<string, FileNode>>();
            long segmentBytes = 0;

            do
            {
                var kvp = nodes[cursor];
                capturedContentVersion[cursor] = kvp.Value.ContentVersion;
                capturedMetadataVersion[cursor] = kvp.Value.MetadataVersion;
                nodeSegmentIndex[cursor] = segmentIndex;
                chunk.Add(kvp);
                segmentBytes += (long)kvp.Value.FileInfo.AllocationSize;
                cursor++;
            }
            while (cursor < nodes.Count && segmentBytes < segmentTargetBytes);

            chunks.Add(chunk);
        }

        // Pass 2: compress/encrypt each segment independently and in parallel — each segment's
        // BuildSegmentPayload call is otherwise single-threaded internally (a segment is sized to
        // produce exactly one ParallelZstd chunk), so parallelism has to come from running
        // multiple segments concurrently rather than from within one.
        var segmentResults = new (int NodeCount, byte[] Payload, byte[] ContentHash, byte[]? Nonce, byte[]? Tag)[chunks.Count];
        ulong writtenBytes = 0;
        var progressLock = new Lock();
        var lastReportedFraction = -1.0;

        try
        {
            Parallel.For(0, chunks.Count, i =>
            {
                var chunk = chunks[i];
                var (finalPayload, contentHash, nonce, tag) = BuildSegmentPayload(
                    chunk,
                    level,
                    customZstdLevel,
                    encryption,
                    node => Interlocked.Add(ref writtenBytes, node.FileInfo.AllocationSize));

                segmentResults[i] = (chunk.Count, finalPayload, contentHash, nonce, tag);
                ReportMonotonic(progress, totalBytes == 0 ? 1.0 : (double)Interlocked.Read(ref writtenBytes) / totalBytes, progressLock, ref lastReportedFraction);
            });
        }
        catch (AggregateException ex)
        {
            throw Unwrap(ex);
        }

        var segments = segmentResults.ToList();

        var tempPath = imagePath + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                BufferSize = FileStreamBufferSize,
            }))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(SegmentedVersion);
                writer.Write((byte)level);
                writer.Write((byte)(encryption is not null ? 1 : 0));
                writer.Write(capacityBytes);
                writer.Write(volumeLabel);

                if (encryption is { } enc)
                {
                    var salt = RandomNumberGenerator.GetBytes(SaltSize);
                    var wrappedCek = WrapCek(enc.Cek, enc.Password, salt, Pbkdf2Iterations, out var wrapNonce, out var wrapTag);
                    writer.Write(salt);
                    writer.Write(Pbkdf2Iterations);
                    writer.Write(wrapNonce);
                    writer.Write(wrapTag);
                    writer.Write(wrappedCek);
                }

                writer.Write(segments.Count);
                foreach (var seg in segments)
                {
                    writer.Write(seg.NodeCount);
                    writer.Write((long)seg.Payload.Length);
                    writer.Write(seg.ContentHash);
                    if (encryption is not null)
                    {
                        writer.Write(seg.Nonce!);
                        writer.Write(seg.Tag!);
                    }
                }

                foreach (var seg in segments)
                {
                    writer.Write(seg.Payload);
                }

                writer.Flush();
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

        // Only reached once the image has actually landed at imagePath. Record what was saved so
        // a future incremental save can tell which nodes are unchanged since this point, without
        // clobbering versions bumped by a WinFsp write that raced with this save (the CAS check
        // below).
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i].Value;
            if (node.SavedContentVersion == ulong.MaxValue || node.SavedContentVersion < capturedContentVersion[i])
            {
                node.SavedContentVersion = capturedContentVersion[i];
            }

            if (node.SavedMetadataVersion == ulong.MaxValue || node.SavedMetadataVersion < capturedMetadataVersion[i])
            {
                node.SavedMetadataVersion = capturedMetadataVersion[i];
            }

            node.SavedSegmentIndex = nodeSegmentIndex[i];
        }

        // Guaranteed final report: an empty disk (chunks.Count == 0) never enters the Parallel.For
        // body above, so nothing else would report 1.0.
        ReportMonotonic(progress, 1.0, progressLock, ref lastReportedFraction);
    }

    private static void SaveSegmentedIncremental(
                                                                FileNodeMap nodeMap,
        ulong capacityBytes,
        string volumeLabel,
        string imagePath,
        ImageCompressionLevel level,
        ImageEncryptionInfo? encryption,
        IProgress<double>? progress,
        int? customZstdLevel,
        long segmentTargetBytes)
    {
        var isEncrypted = encryption is not null;

        if (!TryOpenOldSegmentIndex(imagePath, level, isEncrypted, out var oldStream, out var oldReader, out var oldSegments))
        {
            SaveSegmented(nodeMap, capacityBytes, volumeLabel, imagePath, level, encryption, progress,
                customZstdLevel, segmentTargetBytes);
            return;
        }

        {
            var nodes = nodeMap.GetAllNodes();

            // Group each live node's array position by the segment it belonged to as of the last
            // save. Positions whose node was never saved (or whose recorded segment no longer
            // exists in the old image) go straight into the rewrite pool.
            var byOldSegment = new Dictionary<int, List<int>>();
            var poolPositions = new List<int>();

            for (var pos = 0; pos < nodes.Count; pos++)
            {
                var savedSegment = nodes[pos].Value.SavedSegmentIndex;
                if (savedSegment < 0 || savedSegment >= oldSegments.Length)
                {
                    // savedSegment < 0 is the ordinary case: the node has never been through a
                    // segmented save. savedSegment >= oldSegments.Length should not happen — it
                    // would mean SavedSegmentIndex bookkeeping elsewhere pointed a node at a
                    // segment that doesn't exist in the image we just read — so flag that case
                    // specifically rather than silently folding it into "never saved".
                    System.Diagnostics.Debug.Assert(
                        savedSegment < 0,
                        $"Node '{nodes[pos].Key}' has SavedSegmentIndex {savedSegment}, out of range for " +
                        $"{oldSegments.Length} segment(s) in the existing image — SavedSegmentIndex bookkeeping bug?");

                    poolPositions.Add(pos);
                    continue;
                }

                if (!byOldSegment.TryGetValue(savedSegment, out var members))
                {
                    byOldSegment[savedSegment] = members = [];
                }

                members.Add(pos);
            }

            // A segment is reusable only if every node it originally held is still present
            // (matching node count rules out removals) and none of them changed (content or
            // metadata) since that save. Anything else — including the still-clean members of a
            // segment disqualified by one dirty sibling — goes into the rewrite pool, since reuse
            // only works at whole-segment granularity.
            var reusable = new bool[oldSegments.Length];
            for (var i = 0; i < oldSegments.Length; i++)
            {
                if (!byOldSegment.TryGetValue(i, out var members))
                {
                    // Every node that was in this segment is gone (all removed, or the whole
                    // segment shrank to nothing) — nothing to reuse or rewrite for it.
                    reusable[i] = false;
                    continue;
                }

                if (members.Count != oldSegments[i].NodeCount)
                {
                    // At least one member of this segment was removed since the last save. Its
                    // surviving members can't be reused as part of it (the old bytes cover a node
                    // that's gone) but they still need to be written somewhere.
                    reusable[i] = false;
                    poolPositions.AddRange(members);
                    continue;
                }

                var clean = true;
                foreach (var pos in members)
                {
                    var node = nodes[pos].Value;
                    if (node.ContentVersion != node.SavedContentVersion || node.MetadataVersion != node.SavedMetadataVersion)
                    {
                        clean = false;
                        break;
                    }
                }

                reusable[i] = clean;
                if (!clean)
                {
                    poolPositions.AddRange(members);
                }
            }

            var totalBytes = nodeMap.GetTotalAllocated();
            ulong writtenBytes = 0;
            var progressLock = new Lock();
            var lastReportedFraction = -1.0;

            // Single sequential pass over the old image's segment payload region: copy the bytes of
            // reusable segments, seek past (never buffer) the rest. Segment order in the file
            // matches oldSegments' order, so no random access is needed. Guarded by try/finally so
            // a corrupted/truncated old image (bad PayloadLength, premature EOF) still closes the
            // handle instead of leaking it on the way out. Reused segments are typically the bulk
            // of a large image's bytes, so progress is reported here too — otherwise a save where
            // most/everything is reused would sit at 0% through this whole pass and then jump
            // straight to done.
            var reusedSegments = new List<(int OldIndex, SegmentIndexEntry Entry, byte[] Payload)>();
            try
            {
                for (var i = 0; i < oldSegments.Length; i++)
                {
                    var entry = oldSegments[i];
                    if (reusable[i])
                    {
                        var payload = ReadSegmentPayload(oldReader, entry.PayloadLength);
                        reusedSegments.Add((i, entry, payload));

                        foreach (var pos in byOldSegment[i])
                        {
                            writtenBytes += nodes[pos].Value.FileInfo.AllocationSize;
                        }

                        ReportMonotonic(progress, totalBytes == 0 ? 1.0 : (double)writtenBytes / totalBytes, progressLock, ref lastReportedFraction);
                    }
                    else
                    {
                        oldReader.BaseStream.Seek(entry.PayloadLength, SeekOrigin.Current);
                    }
                }
            }
            finally
            {
                // Close the old image now, before opening the temp file below, whether or not the
                // loop above succeeded — Windows refuses to replace a file over an open handle to
                // it (even a shared-read one), so this must happen before the File.Move that
                // follows, and it must still happen if the loop threw.
                oldReader.Dispose();
                oldStream.Dispose();
            }

            poolPositions.Sort();

            var capturedContentVersion = new ulong[nodes.Count];
            var capturedMetadataVersion = new ulong[nodes.Count];

            // Pass 1: decide rewrite-pool segment boundaries only (pure in-memory bookkeeping),
            // same rationale as SaveSegmented above.
            var poolChunks = new List<List<int>>();
            var cursor = 0;

            while (cursor < poolPositions.Count)
            {
                var chunkPositions = new List<int>();
                long segmentBytes = 0;

                do
                {
                    var pos = poolPositions[cursor];
                    var node = nodes[pos].Value;
                    capturedContentVersion[pos] = node.ContentVersion;
                    capturedMetadataVersion[pos] = node.MetadataVersion;
                    chunkPositions.Add(pos);
                    segmentBytes += (long)node.FileInfo.AllocationSize;
                    cursor++;
                }
                while (cursor < poolPositions.Count && segmentBytes < segmentTargetBytes);

                poolChunks.Add(chunkPositions);
            }

            // Pass 2: compress/encrypt each rewrite-pool segment independently and in parallel —
            // see SaveSegmented for why parallelism must be at segment granularity.
            var newSegmentResults = new (List<int> Positions, byte[] Payload, byte[] ContentHash, byte[]? Nonce, byte[]? Tag)[poolChunks.Count];

            try
            {
                Parallel.For(0, poolChunks.Count, i =>
                {
                    var chunkPositions = poolChunks[i];
                    var (finalPayload, contentHash, nonce, tag) = BuildSegmentPayload(
                        chunkPositions.Select(pos => nodes[pos]),
                        level,
                        customZstdLevel,
                        encryption,
                        node => Interlocked.Add(ref writtenBytes, node.FileInfo.AllocationSize));

                    newSegmentResults[i] = (chunkPositions, finalPayload, contentHash, nonce, tag);
                    ReportMonotonic(progress, totalBytes == 0 ? 1.0 : (double)Interlocked.Read(ref writtenBytes) / totalBytes, progressLock, ref lastReportedFraction);
                });
            }
            catch (AggregateException ex)
            {
                throw Unwrap(ex);
            }

            var newSegments = newSegmentResults.ToList();

            var tempPath = imagePath + ".tmp";
            try
            {
                using (var stream = new FileStream(tempPath, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    BufferSize = FileStreamBufferSize,
                }))
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Magic);
                    writer.Write(SegmentedVersion);
                    writer.Write((byte)level);
                    writer.Write((byte)(encryption is not null ? 1 : 0));
                    writer.Write(capacityBytes);
                    writer.Write(volumeLabel);

                    if (encryption is { } enc)
                    {
                        var salt = RandomNumberGenerator.GetBytes(SaltSize);
                        var wrappedCek = WrapCek(enc.Cek, enc.Password, salt, Pbkdf2Iterations, out var wrapNonce, out var wrapTag);
                        writer.Write(salt);
                        writer.Write(Pbkdf2Iterations);
                        writer.Write(wrapNonce);
                        writer.Write(wrapTag);
                        writer.Write(wrappedCek);
                    }

                    writer.Write(reusedSegments.Count + newSegments.Count);

                    foreach (var (_, entry, _) in reusedSegments)
                    {
                        writer.Write(entry.NodeCount);
                        writer.Write(entry.PayloadLength);
                        writer.Write(entry.ContentHash);
                        if (encryption is not null)
                        {
                            writer.Write(entry.Nonce!);
                            writer.Write(entry.Tag!);
                        }
                    }

                    foreach (var seg in newSegments)
                    {
                        writer.Write(seg.Positions.Count);
                        writer.Write((long)seg.Payload.Length);
                        writer.Write(seg.ContentHash);
                        if (encryption is not null)
                        {
                            writer.Write(seg.Nonce!);
                            writer.Write(seg.Tag!);
                        }
                    }

                    foreach (var (_, _, payload) in reusedSegments)
                    {
                        writer.Write(payload);
                    }

                    foreach (var seg in newSegments)
                    {
                        writer.Write(seg.Payload);
                    }

                    writer.Flush();
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

            // Reused-segment members are left untouched: their SavedContentVersion/
            // SavedMetadataVersion already match what was just persisted (that equality was the
            // reuse criterion above), and touching them here could wrongly launder a mutation that
            // raced in after the criterion was checked but before this point — the bytes actually
            // on disk for that node are still the pre-race ones. Only the segment index they now
            // live at needs updating.
            var finalSegmentIndex = 0;
            foreach (var (oldIndex, _, _) in reusedSegments)
            {
                foreach (var pos in byOldSegment[oldIndex])
                {
                    nodes[pos].Value.SavedSegmentIndex = finalSegmentIndex;
                }

                finalSegmentIndex++;
            }

            foreach (var seg in newSegments)
            {
                foreach (var pos in seg.Positions)
                {
                    var node = nodes[pos].Value;
                    if (node.SavedContentVersion == ulong.MaxValue || node.SavedContentVersion < capturedContentVersion[pos])
                    {
                        node.SavedContentVersion = capturedContentVersion[pos];
                    }

                    if (node.SavedMetadataVersion == ulong.MaxValue || node.SavedMetadataVersion < capturedMetadataVersion[pos])
                    {
                        node.SavedMetadataVersion = capturedMetadataVersion[pos];
                    }

                    node.SavedSegmentIndex = finalSegmentIndex;
                }

                finalSegmentIndex++;
            }

            // Guaranteed final report: if every segment was reused (poolChunks empty) or the image
            // is empty (totalBytes == 0), nothing above may have reported 1.0 yet.
            ReportMonotonic(progress, 1.0, progressLock, ref lastReportedFraction);
        }
    }

    /// <summary>
    /// Opens <paramref name="imagePath"/> and reads through its header and segment index, iff it is
    /// a version 6 image whose compression level and encryption state match the current save
    /// request. Positioned at the start of the segment payload region on success. Never reads or
    /// requires a password — reusable segments are copied as opaque bytes, never decrypted.
    /// </summary>
    private static bool TryOpenOldSegmentIndex(
        string imagePath,
        ImageCompressionLevel level,
        bool isEncrypted,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FileStream? stream,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BinaryReader? reader,
        out SegmentIndexEntry[] segments)
    {
        stream = null;
        reader = null;
        segments = [];

        if (!File.Exists(imagePath))
        {
            return false;
        }

        FileStream? candidateStream = null;
        try
        {
            candidateStream = new(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var candidateReader = new BinaryReader(candidateStream, System.Text.Encoding.UTF8, leaveOpen: true);

            var magic = candidateReader.ReadBytes(4);
            if (!magic.SequenceEqual(Magic))
            {
                candidateStream.Dispose();
                return false;
            }

            var version = candidateReader.ReadInt32();
            if (version != SegmentedVersion)
            {
                candidateStream.Dispose();
                return false;
            }

            var oldLevel = (ImageCompressionLevel)candidateReader.ReadByte();
            var oldIsEncrypted = candidateReader.ReadByte() != 0;
            if (oldLevel != level || oldIsEncrypted != isEncrypted)
            {
                candidateStream.Dispose();
                return false;
            }

            candidateReader.ReadUInt64(); // capacity — always rewritten fresh, not compared
            candidateReader.ReadString(); // volume label — always rewritten fresh, not compared

            if (isEncrypted)
            {
                // Key-wrap fields are rewritten fresh on every save (new salt/wrap-nonce), so their
                // values are irrelevant here — only their fixed byte length matters, to reach the
                // segment index. Reused segments' ciphertext stays valid regardless, since it was
                // never wrapped by these fields — only the content-encryption key (assumed, by
                // caller contract, to be the same key across saves once encryption is enabled) was
                // ever used to encrypt segment payloads.
                candidateReader.ReadBytes(SaltSize);
                candidateReader.ReadInt32();
                candidateReader.ReadBytes(NonceSize);
                candidateReader.ReadBytes(TagSize);
                candidateReader.ReadBytes(CekSize);
            }

            var segmentCount = candidateReader.ReadInt32();
            var entries = new SegmentIndexEntry[segmentCount];
            for (var i = 0; i < segmentCount; i++)
            {
                var nodeCount = candidateReader.ReadInt32();
                var payloadLength = candidateReader.ReadInt64();
                var contentHash = candidateReader.ReadBytes(Sha256Size);
                byte[]? nonce = null;
                byte[]? tag = null;
                if (isEncrypted)
                {
                    nonce = candidateReader.ReadBytes(NonceSize);
                    tag = candidateReader.ReadBytes(TagSize);
                }

                entries[i] = new(nodeCount, payloadLength, contentHash, nonce, tag);
            }

            if (!SegmentIndexMatchesPayloadRegion(entries, candidateStream.Length - candidateStream.Position))
            {
                // A truncated (or otherwise damaged) old image: reusing it would fail mid-copy on
                // every save, forever, since the file never gets replaced. A full rewrite from
                // the in-memory tree replaces it instead.
                Logger.LogWarning(
                    "Existing image '{ImagePath}' has a segment index inconsistent with its size; rewriting it in full.",
                    imagePath);
                candidateStream.Dispose();
                return false;
            }

            stream = candidateStream;
            reader = candidateReader;
            segments = entries;
            return true;
        }
        catch
        {
            candidateStream?.Dispose();
            return false;
        }
    }

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
        // The disk stays mounted while it's saved, so capture the node's metadata and content
        // reference once (Overwrite can swap FileData, a write can resize it) and record sizes
        // describing exactly the bytes stored below. Otherwise a file growing mid-save records an
        // AllocationSize smaller than its data, and the image fails to load. CopyTo writes exactly
        // the prefixed length even if the content shrinks meanwhile.
        var info = node.FileInfo;
        var data = node.IsDirectory ? null : node.FileData;
        var length = data is null ? 0UL : Math.Min(info.FileSize, (ulong)data.Length);

        if (!node.IsDirectory)
        {
            info.FileSize = length;
            info.AllocationSize = Math.Max(info.AllocationSize, FileNode.AlignToAllocationUnit(length));
        }

        NodeMetadataIO.WriteMetadata(writer, path, info, node.FileSecurity);
        writer.Write((long)length);

        if (length > 0)
        {
            data!.CopyTo(writer.BaseStream, (long)length);
        }
    }

    /// <summary>
    /// Writes the node-count-prefixed node region for <paramref name="nodeMap"/> directly into
    /// <paramref name="target"/>, Zstd-compressing on the fly (in parallel across chunks, see
    /// <see cref="ParallelZstd"/>) when <paramref name="compress"/> is set. Never materializes the
    /// whole region as a single in-memory buffer, so disk content of any size can be saved
    /// regardless of the ~2 GB limit on <see cref="MemoryStream"/>/managed arrays. The
    /// <see cref="ParallelZstd.WriteStream"/> (when used) is explicitly disposed here — rather than
    /// relying on <see cref="BinaryWriter"/>'s own disposal with <c>leaveOpen: true</c>, which
    /// would skip it — so every outstanding chunk is compressed and flushed before
    /// <paramref name="target"/> is used for anything else.
    /// </summary>
    private static void WriteNodeRegion(
        Stream target,
        bool compress,
        ImageCompressionLevel level,
        int? customZstdLevel,
        FileNodeMap nodeMap,
        IProgress<double>? progress)
    {
        var payloadStream = compress
            ? new ParallelZstd.WriteStream(target, level.ToZstdLevel(customZstdLevel))
            : target;

        try
        {
            using var payloadWriter = new BinaryWriter(payloadStream, System.Text.Encoding.UTF8, leaveOpen: true);

            var nodes = nodeMap.GetAllNodes();
            payloadWriter.Write(nodes.Count);

            var totalBytes = nodeMap.GetTotalAllocated();

            if (nodes.Count == 0 || totalBytes == 0)
            {
                // Nothing to weight progress by (e.g. all-empty-directories case) — fall back to
                // a simple per-node fraction so progress still reaches 1.0 deterministically.
                var written = 0;
                foreach (var kvp in nodes)
                {
                    WriteNode(payloadWriter, kvp.Key, kvp.Value);
                    written++;
                    progress?.Report(nodes.Count == 0 ? 1.0 : (double)written / nodes.Count);
                }

                if (nodes.Count == 0)
                {
                    progress?.Report(1.0);
                }
            }
            else
            {
                ulong writtenBytes = 0;
                foreach (var kvp in nodes)
                {
                    WriteNode(payloadWriter, kvp.Key, kvp.Value);
                    writtenBytes += kvp.Value.FileInfo.AllocationSize;
                    progress?.Report((double)writtenBytes / totalBytes);
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
    /// Returns whether <paramref name="entries"/> describe exactly the
    /// <paramref name="payloadRegionBytes"/> bytes that follow the segment index: every node count
    /// and payload length non-negative, every payload small enough for
    /// <see cref="ReadSegmentPayload"/>, and the payload lengths summing to the region's size. The
    /// writer never appends anything after the last payload, so any mismatch means the image was
    /// truncated or damaged.
    /// </summary>
    private static bool SegmentIndexMatchesPayloadRegion(SegmentIndexEntry[] entries, long payloadRegionBytes)
    {
        var total = 0L;
        foreach (var entry in entries)
        {
            if (entry.NodeCount < 0 || entry.PayloadLength < 0 || entry.PayloadLength > int.MaxValue)
            {
                return false;
            }

            total += entry.PayloadLength;
            if (total > payloadRegionBytes)
            {
                return false;
            }
        }

        return total == payloadRegionBytes;
    }

    /// <summary>
    /// Reads exactly <paramref name="payloadLength"/> bytes of a segment's payload from
    /// <paramref name="reader"/>. Unlike <see cref="BinaryReader.ReadBytes"/> alone, which returns
    /// a shorter array instead of throwing when the underlying stream ends early, this verifies
    /// the full length was read so a truncated/corrupted image fails loudly here rather than
    /// silently propagating a too-short payload alongside a stale, too-large recorded length.
    /// </summary>
    private static byte[] ReadSegmentPayload(BinaryReader reader, long payloadLength)
    {
        var payload = reader.ReadBytes(checked((int)payloadLength));
        if (payload.Length != payloadLength)
        {
            throw new EndOfStreamException("The image ends before its last segment.");
        }

        return payload;
    }

    private readonly record struct SegmentIndexEntry(
        int NodeCount,
        long PayloadLength,
        byte[] ContentHash,
        byte[]? Nonce,
        byte[]? Tag);
}
