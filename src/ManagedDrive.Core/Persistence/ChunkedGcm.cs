using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ManagedDrive.Core.Persistence;

/// <summary>
/// Shared chunked AES-256-GCM read/write stream pair used to encrypt/decrypt data of any size
/// without ever holding the whole plaintext or ciphertext in a single buffer — <see cref="AesGcm"/>
/// only exposes single-shot <c>Encrypt</c>/<c>Decrypt</c> over one buffer, and a managed array is
/// itself practically capped near 2 GB, so this splits the data into independently
/// encrypted/authenticated chunks instead. Used by both <see cref="DiskImageSerializer"/> (the
/// node region of a <c>.mdr</c> image) and <see cref="Snapshots.SnapshotStore"/> (individual
/// content-addressed file blobs).
/// </summary>
internal static class ChunkedGcm
{
    internal const int NonceSize = 12;
    internal const int TagSize = 16;

    /// <summary>
    /// Size of each independently AES-GCM-encrypted chunk. Kept well under 2 GB so no single
    /// chunk buffer approaches managed-array or <see cref="AesGcm"/> single-shot limits.
    /// Overridable by tests via <see cref="TestChunkSizeOverride"/> to exercise the multi-chunk
    /// path without allocating a real 64 MB buffer.
    /// </summary>
    private const int DefaultChunkSize = 64 * 1024 * 1024;

    /// <summary>
    /// Test-only override for <see cref="DefaultChunkSize"/>; <see langword="null"/> means use the
    /// production default. Set via <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static int? TestChunkSizeOverride;

    internal static int ChunkSize => TestChunkSizeOverride ?? DefaultChunkSize;

    /// <summary>
    /// Derives a unique nonce for chunk <paramref name="chunkIndex"/> from a random per-save
    /// <paramref name="baseNonce"/> by XOR-ing its last 4 bytes with the big-endian chunk index.
    /// This is a standard segmented-AEAD nonce derivation: as long as <paramref name="baseNonce"/>
    /// is freshly random per save and chunk indices are never reused within that save (both true
    /// here — <see cref="WriteStream"/> increments a private counter once per chunk), every chunk
    /// gets a distinct nonce under the same key, which is AES-GCM's only requirement.
    /// </summary>
    internal static byte[] DeriveChunkNonce(byte[] baseNonce, int chunkIndex)
    {
        var nonce = new byte[NonceSize];
        DeriveChunkNonce(baseNonce, chunkIndex, nonce);
        return nonce;
    }

    /// <summary>
    /// <see cref="DeriveChunkNonce(byte[], int)"/> into a caller-supplied buffer, so the per-chunk
    /// stream paths can derive into a stack buffer instead of allocating.
    /// </summary>
    private static void DeriveChunkNonce(ReadOnlySpan<byte> baseNonce, int chunkIndex, Span<byte> nonce)
    {
        baseNonce[..NonceSize].CopyTo(nonce);
        Span<byte> indexBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(indexBytes, (uint)chunkIndex);

        for (var i = 0; i < indexBytes.Length; i++)
        {
            nonce[NonceSize - indexBytes.Length + i] ^= indexBytes[i];
        }
    }

    /// <summary>
    /// Write-only <see cref="Stream"/> that buffers up to <see cref="ChunkSize"/> bytes at a
    /// time and, on each full buffer plus once more on <see cref="Complete"/>, AES-256-GCM-encrypts
    /// that chunk with a nonce derived via <see cref="DeriveChunkNonce"/> and writes it to the
    /// underlying stream as <c>[Int32 ciphertext length][16-byte tag][ciphertext]</c>.
    /// </summary>
    internal sealed class WriteStream(Stream output, byte[] key, byte[] baseNonce, int chunkSize) : Stream
    {
        // One cipher instance for the whole stream rather than one per chunk, so the key
        // schedule is computed once.
        private readonly AesGcm _aesGcm = new(key, TagSize);
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

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        // Overridden (rather than left to Stream's default, which rents a pooled array and copies
        // the span into it first) because FileContent.CopyTo writes spans straight from its chunks.
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                var toCopy = Math.Min(buffer.Length, _buffer.Length - _bufferLength);
                buffer[..toCopy].CopyTo(_buffer.AsSpan(_bufferLength));
                _bufferLength += toCopy;
                buffer = buffer[toCopy..];

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

            WriteChunk(Span<byte>.Empty);
            _completed = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _aesGcm.Dispose();
            }

            base.Dispose(disposing);
        }

        private void FlushChunk()
        {
            WriteChunk(_buffer.AsSpan(0, _bufferLength));
            _bufferLength = 0;
        }

        /// <summary>
        /// Encrypts <paramref name="chunk"/> in place and writes it out. Encrypting in place (rather
        /// than into a fresh chunk-sized ciphertext array) also overwrites the plaintext, so the
        /// buffer never keeps it around once the chunk has been written.
        /// </summary>
        private void WriteChunk(Span<byte> chunk)
        {
            Span<byte> nonce = stackalloc byte[NonceSize];
            DeriveChunkNonce(baseNonce, _chunkIndex, nonce);
            Span<byte> tag = stackalloc byte[TagSize];

            _aesGcm.Encrypt(nonce, chunk, chunk, tag);

            Span<byte> lengthBytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, chunk.Length);
            output.Write(lengthBytes);
            output.Write(tag);
            if (!chunk.IsEmpty)
            {
                output.Write(chunk);
            }

            _chunkIndex++;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Read-only <see cref="Stream"/> counterpart to <see cref="WriteStream"/>: reads the
    /// <c>[length][tag][ciphertext]</c> chunk sequence from the underlying stream, decrypting
    /// each chunk with the matching derived nonce and exposing the concatenated plaintext as a
    /// normal readable stream (typically wrapped by a decompressing <see cref="System.IO.Compression.GZipStream"/>).
    /// Throws <see cref="CryptographicException"/> if any chunk's tag fails to authenticate —
    /// callers should translate that into their own password-incorrect exception type.
    /// </summary>
    internal sealed class ReadStream(Stream source, byte[] key, byte[] baseNonce) : Stream
    {
        private readonly AesGcm _aesGcm = new(key, TagSize);

        /// <summary>
        /// Reused across chunks: each chunk's ciphertext is read into it and decrypted in place,
        /// so a stream of equal-sized chunks allocates this buffer once instead of a ciphertext
        /// and a plaintext array per chunk.
        /// </summary>
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

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        // Overridden (rather than left to Stream's default, which rents a pooled array and copies
        // through it) because BinaryReader reads every primitive field through this overload.
        public override int Read(Span<byte> buffer)
        {
            var totalRead = 0;

            while (!buffer.IsEmpty)
            {
                if (_positionInChunk == _currentChunkLength)
                {
                    if (_endOfStream || !TryReadNextChunk())
                    {
                        break;
                    }
                }

                var toCopy = Math.Min(buffer.Length, _currentChunkLength - _positionInChunk);
                _currentChunk.AsSpan(_positionInChunk, toCopy).CopyTo(buffer);
                _positionInChunk += toCopy;
                buffer = buffer[toCopy..];
                totalRead += toCopy;
            }

            return totalRead;
        }

        // Overridden because Stream's default allocates a one-byte array per call, and
        // BinaryReader.ReadString reads its length prefix a byte at a time.
        public override int ReadByte()
        {
            if (_positionInChunk == _currentChunkLength && (_endOfStream || !TryReadNextChunk()))
            {
                return -1;
            }

            return _currentChunk[_positionInChunk++];
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _aesGcm.Dispose();
            }

            base.Dispose(disposing);
        }

        private bool TryReadNextChunk()
        {
            Span<byte> lengthBytes = stackalloc byte[4];
            source.ReadExactly(lengthBytes);
            var ciphertextLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (ciphertextLength < 0)
            {
                throw new InvalidDataException($"Invalid encrypted chunk length: {ciphertextLength}.");
            }

            Span<byte> tag = stackalloc byte[TagSize];
            source.ReadExactly(tag);

            if (_currentChunk.Length < ciphertextLength)
            {
                _currentChunk = new byte[ciphertextLength];
            }

            var chunk = _currentChunk.AsSpan(0, ciphertextLength);
            source.ReadExactly(chunk);

            Span<byte> nonce = stackalloc byte[NonceSize];
            DeriveChunkNonce(baseNonce, _chunkIndex, nonce);
            _aesGcm.Decrypt(nonce, chunk, tag, chunk);

            _chunkIndex++;
            _positionInChunk = 0;
            _currentChunkLength = ciphertextLength;

            if (ciphertextLength == 0)
            {
                _endOfStream = true;
                return false;
            }

            return true;
        }

        public override void Flush() => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
