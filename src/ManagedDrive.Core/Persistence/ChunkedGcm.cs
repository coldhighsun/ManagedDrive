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
        var nonce = (byte[])baseNonce.Clone();
        Span<byte> indexBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(indexBytes, (uint)chunkIndex);

        for (var i = 0; i < indexBytes.Length; i++)
        {
            nonce[NonceSize - indexBytes.Length + i] ^= indexBytes[i];
        }

        return nonce;
    }

    /// <summary>
    /// Write-only <see cref="Stream"/> that buffers up to <see cref="ChunkSize"/> bytes at a
    /// time and, on each full buffer plus once more on <see cref="Complete"/>, AES-256-GCM-encrypts
    /// that chunk with a nonce derived via <see cref="DeriveChunkNonce"/> and writes it to the
    /// underlying stream as <c>[Int32 ciphertext length][16-byte tag][ciphertext]</c>.
    /// </summary>
    internal sealed class WriteStream(Stream output, byte[] key, byte[] baseNonce, int chunkSize) : Stream
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
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, ciphertext.Length);
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
    /// Read-only <see cref="Stream"/> counterpart to <see cref="WriteStream"/>: reads the
    /// <c>[length][tag][ciphertext]</c> chunk sequence from the underlying stream, decrypting
    /// each chunk with the matching derived nonce and exposing the concatenated plaintext as a
    /// normal readable stream (typically wrapped by a decompressing <see cref="System.IO.Compression.GZipStream"/>).
    /// Throws <see cref="CryptographicException"/> if any chunk's tag fails to authenticate —
    /// callers should translate that into their own password-incorrect exception type.
    /// </summary>
    internal sealed class ReadStream(Stream source, byte[] key, byte[] baseNonce) : Stream
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
            var ciphertextLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);

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
