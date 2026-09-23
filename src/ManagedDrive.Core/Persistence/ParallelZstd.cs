using System.Buffers.Binary;

namespace ManagedDrive.Core.Persistence;

/// <summary>
/// Write/read helper pair that compresses/decompresses a stream of bytes as a sequence of
/// independently compressed Zstd chunks, each framed as <c>[Int32 compressed length][compressed
/// bytes]</c> and terminated by a zero-length chunk (mirroring <see cref="ChunkedGcm"/>'s framing),
/// processed concurrently across a bounded worker pool while still emitting/consuming them in
/// original order. This turns both image/snapshot save and load (otherwise single-threaded
/// bottlenecks with plain <see cref="ZstdSharp.CompressionStream"/>/<see cref="ZstdSharp.DecompressionStream"/>)
/// into parallelizable operations. The explicit length framing (rather than relying on
/// concatenated-frame auto-detection, as a plain <see cref="ZstdSharp.DecompressionStream"/> would
/// need to scan for) is what lets <see cref="ReadStream"/> dispatch each chunk's decompression to
/// the thread pool without first decompressing anything to find chunk boundaries.
/// Used by <see cref="DiskImageSerializer"/> (the node region of a <c>.mdr</c> image) and
/// <see cref="Snapshots.SnapshotStore"/> (individual content-addressed file blobs).
/// </summary>
internal static class ParallelZstd
{
    /// <summary>
    /// Test-only override for <see cref="DefaultChunkSize"/>; <see langword="null"/> means use the
    /// production default. Set via <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static int? TestChunkSizeOverride;

    /// <summary>
    /// Size of each independently compressed chunk. Large enough that per-chunk compression
    /// overhead (frame header/epilogue, a fresh <see cref="ZstdSharp.Compressor"/> context) stays
    /// negligible relative to the data compressed, but small enough to get real parallelism on
    /// typical disk-image sizes. Overridable by tests via <see cref="TestChunkSizeOverride"/> to
    /// exercise the multi-chunk path without allocating real multi-megabyte buffers.
    /// </summary>
    private const int DefaultChunkSize = 4 * 1024 * 1024;

    internal static int ChunkSize => TestChunkSizeOverride ?? DefaultChunkSize;

    /// <summary>
    /// One-shot equivalent of writing <paramref name="length"/> bytes of <paramref name="data"/>
    /// through a <see cref="WriteStream"/>: produces the identical <c>[length][compressed bytes]
    /// ... [0]</c> chunk sequence, but compresses each chunk straight out of
    /// <paramref name="data"/> instead of first copying it into a staging buffer, and returns an
    /// exactly-sized array rather than one that went through a growable
    /// <see cref="MemoryStream"/>. A single-chunk input (the common case for a v6 image segment)
    /// is compressed inline on the calling thread; multiple chunks are compressed in parallel.
    /// </summary>
    /// <param name="data">Buffer holding the bytes to compress.</param>
    /// <param name="length">Number of leading bytes of <paramref name="data"/> to compress.</param>
    /// <param name="level">Zstd compression level.</param>
    /// <param name="chunkSize">Chunk size override for tests; defaults to <see cref="ChunkSize"/>.</param>
    /// <returns>The framed compressed payload.</returns>
    internal static byte[] CompressFramed(byte[] data, int length, int level, int? chunkSize = null)
    {
        var size = chunkSize ?? ChunkSize;
        var chunkCount = (int)(((long)length + size - 1) / size);
        var compressed = new (byte[] Buffer, int Length)[chunkCount];

        if (chunkCount == 1)
        {
            // Kept at compress-bound size: it's copied into the exact-size result below right away,
            // so there's no point trimming it first.
            compressed[0] = CompressToBound(data.AsSpan(0, length), level);
        }
        else if (chunkCount > 1)
        {
            Parallel.For(0, chunkCount, i =>
            {
                var offset = i * size;
                var (buffer, written) = CompressToBound(data.AsSpan(offset, Math.Min(size, length - offset)), level);

                // Trim now so all chunks' compress-bound buffers aren't alive at once.
                compressed[i] = (buffer.AsSpan(0, written).ToArray(), written);
            });
        }

        long total = sizeof(int);
        foreach (var (_, written) in compressed)
        {
            total += sizeof(int) + written;
        }

        var result = new byte[checked((int)total)];
        var position = 0;
        foreach (var (buffer, written) in compressed)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(position), written);
            buffer.AsSpan(0, written).CopyTo(result.AsSpan(position + sizeof(int)));
            position += sizeof(int) + written;
        }

        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(position), 0);
        return result;
    }

    private static (byte[] Buffer, int Length) CompressToBound(ReadOnlySpan<byte> source, int level)
    {
        using var compressor = new ZstdSharp.Compressor(level);
        var buffer = new byte[ZstdSharp.Compressor.GetCompressBound(source.Length)];
        return (buffer, compressor.Wrap(source, buffer));
    }

    /// <summary>
    /// Read-only counterpart to <see cref="WriteStream"/>: reads the
    /// <c>[length][compressed bytes]</c> chunk sequence written by it, decompressing chunks on a
    /// bounded worker pool while yielding decompressed bytes in original order. Chunk headers are
    /// read sequentially off <paramref name="source"/> (negligible cost), and up to
    /// <paramref name="maxDegreeOfParallelism"/> chunks are kept in flight at once so decompression
    /// of later chunks overlaps with the caller consuming earlier ones — the same prefetch pattern
    /// <see cref="WriteStream"/> uses for compression, mirrored for the read side.
    /// </summary>
    internal sealed class ReadStream(Stream source, int? maxDegreeOfParallelism = null) : Stream
    {
        private readonly int _maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism ?? Environment.ProcessorCount);
        private readonly Queue<Task<byte[]>> _pending = new();
        private byte[] _currentChunk = [];
        private bool _endOfStream;
        private int _positionInChunk;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var totalRead = 0;

            while (count > 0)
            {
                if (_positionInChunk == _currentChunk.Length)
                {
                    // Not gated on _endOfStream alone: that flag only means "no more chunk
                    // headers left to read," but chunks already prefetched into _pending (read
                    // ahead of the terminator) still need to be drained.
                    if (!TryAdvanceChunk())
                    {
                        break;
                    }
                }

                var toCopy = Math.Min(count, _currentChunk.Length - _positionInChunk);
                Array.Copy(_currentChunk, _positionInChunk, buffer, offset, toCopy);
                _positionInChunk += toCopy;
                offset += toCopy;
                count -= toCopy;
                totalRead += toCopy;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static byte[] Decompress(byte[] compressed)
        {
            using var decompressor = new ZstdSharp.Decompressor();
            return decompressor.Unwrap(compressed).ToArray();
        }

        private void FillPending()
        {
            while (!_endOfStream && _pending.Count < _maxDegreeOfParallelism)
            {
                if (!TryQueueNextChunk())
                {
                    break;
                }
            }
        }

        private bool TryAdvanceChunk()
        {
            FillPending();

            if (_pending.Count == 0)
            {
                _endOfStream = true;
                return false;
            }

            _currentChunk = _pending.Dequeue().GetAwaiter().GetResult();
            _positionInChunk = 0;

            // Immediately queue the next chunk so decompression of what's now the tail of the
            // pending queue overlaps with the caller consuming _currentChunk.
            FillPending();

            return _currentChunk.Length > 0 || TryAdvanceChunk();
        }

        private bool TryQueueNextChunk()
        {
            if (_endOfStream)
            {
                return false;
            }

            Span<byte> lengthBytes = stackalloc byte[4];
            source.ReadExactly(lengthBytes);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);

            if (length == 0)
            {
                _endOfStream = true;
                return false;
            }

            var chunk = new byte[length];
            source.ReadExactly(chunk);

            _pending.Enqueue(Task.Run(() => Decompress(chunk)));
            return true;
        }
    }

    /// <summary>
    /// Write-only <see cref="Stream"/> that buffers up to <see cref="ChunkSize"/> bytes at a time
    /// and, on each full buffer plus once more on <see cref="Complete"/>, hands that chunk to a
    /// background <see cref="Task"/> that Zstd-compresses it independently. Compression runs
    /// concurrently (bounded by <paramref name="maxDegreeOfParallelism"/>), but chunks are always
    /// written to <paramref name="target"/> in the order they were queued, blocking on the oldest
    /// outstanding task if the queue is full — so output ordering matches input ordering
    /// regardless of which task happens to finish first.
    /// </summary>
    internal sealed class WriteStream(Stream target, int level, int? maxDegreeOfParallelism = null) : Stream
    {
        private readonly int _maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism ?? Environment.ProcessorCount);
        private readonly Queue<Task<byte[]>> _pending = new();
        private byte[] _buffer = new byte[ChunkSize];
        private int _bufferLength;
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

        /// <summary>
        /// Flushes any remaining buffered bytes as a final chunk, then waits for all pending
        /// </summary>
        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            if (_bufferLength > 0)
            {
                QueueChunk();
            }

            while (_pending.Count > 0)
            {
                DrainOne();
            }

            WriteChunkHeader(0);
            _completed = true;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

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

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_completed)
            {
                Complete();
            }

            base.Dispose(disposing);
        }

        private static byte[] Compress(byte[] data, int length, int level)
        {
            using var compressor = new ZstdSharp.Compressor(level);
            return compressor.Wrap(data.AsSpan(0, length)).ToArray();
        }

        private void DrainOne()
        {
            var compressed = _pending.Dequeue().GetAwaiter().GetResult();
            WriteChunkHeader(compressed.Length);
            target.Write(compressed, 0, compressed.Length);
        }

        private void FlushChunk()
        {
            if (_pending.Count >= _maxDegreeOfParallelism)
            {
                DrainOne();
            }

            QueueChunk();
        }

        private void QueueChunk()
        {
            var chunk = _buffer;
            var length = _bufferLength;
            _buffer = new byte[ChunkSize];
            _bufferLength = 0;

            _pending.Enqueue(Task.Run(() => Compress(chunk, length, level)));
        }

        private void WriteChunkHeader(int length)
        {
            Span<byte> lengthBytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, length);
            target.Write(lengthBytes);
        }
    }
}
