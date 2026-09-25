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
    /// Backing store for <see cref="TestChunkSizeOverride"/>. An <see cref="AsyncLocal{T}"/>
    /// rather than a plain static field so that tests running concurrently (different test
    /// classes/collections, or parallel test execution) each see only their own override instead
    /// of racing on a single shared value — the value set here flows to any code called from the
    /// same logical call chain, including work queued via <see cref="Task.Run(Action)"/>, but
    /// never to an unrelated, concurrently-running chain.
    /// </summary>
    private static readonly AsyncLocal<int?> _testChunkSizeOverride = new();

    /// <summary>
    /// Test-only override for <see cref="DefaultChunkSize"/>; <see langword="null"/> means use the
    /// production default. Set via <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static int? TestChunkSizeOverride
    {
        get => _testChunkSizeOverride.Value;
        set => _testChunkSizeOverride.Value = value;
    }

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
            using var compressor = new ZstdSharp.Compressor(level);
            compressed[0] = CompressToBound(compressor, data.AsSpan(0, length));
        }
        else if (chunkCount > 1)
        {
            // One compressor per worker rather than per chunk: a Zstd compression context is
            // expensive to set up (and large at high levels), and Wrap starts a fresh frame on
            // every call anyway, so reusing it across a worker's chunks is safe.
            Parallel.For(
                0,
                chunkCount,
                () => new ZstdSharp.Compressor(level),
                (i, _, compressor) =>
                {
                    var offset = i * size;
                    var (buffer, written) = CompressToBound(compressor, data.AsSpan(offset, Math.Min(size, length - offset)));

                    // Trim now so all chunks' compress-bound buffers aren't alive at once.
                    compressed[i] = (buffer.AsSpan(0, written).ToArray(), written);
                    return compressor;
                },
                compressor => compressor.Dispose());
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

    private static (byte[] Buffer, int Length) CompressToBound(ZstdSharp.Compressor compressor, ReadOnlySpan<byte> source)
    {
        var buffer = new byte[ZstdSharp.Compressor.GetCompressBound(source.Length)];
        return (buffer, compressor.Wrap(source, buffer));
    }

    /// <summary>
    /// Pops a buffer of at least <paramref name="size"/> bytes off <paramref name="pool"/>, or
    /// allocates one of <paramref name="capacity"/> bytes (grown to cover <paramref name="size"/>)
    /// if the pool is empty or its top buffer is too small. Sizing new buffers to the largest
    /// request seen so far keeps variable-size requests from repeatedly discarding pooled buffers.
    /// </summary>
    private static byte[] RentBuffer(Stack<byte[]> pool, int size, ref int capacity)
    {
        if (pool.TryPop(out var pooled))
        {
            if (pooled.Length >= size)
            {
                return pooled;
            }

            // Too small for this request, but still a perfectly good buffer for a future,
            // smaller one — put it back rather than discarding it.
            pool.Push(pooled);
        }

        capacity = Math.Max(capacity, size);
        return new byte[capacity];
    }

    /// <summary>
    /// Pops an entry off <paramref name="pool"/>, or creates one via <paramref name="factory"/> if
    /// it's empty. Fixed-size counterpart to <see cref="RentBuffer"/> for pools (compressors,
    /// decompressors, fixed-size byte arrays) that don't need <see cref="RentBuffer"/>'s
    /// grow-to-largest-request tracking.
    /// </summary>
    private static T RentOrCreate<T>(Stack<T> pool, Func<T> factory) => pool.TryPop(out var pooled) ? pooled : factory();

    /// <summary>
    /// <see cref="RentOrCreate{T}(Stack{T}, Func{T})"/> overload for a factory that needs a value
    /// from the caller (e.g. a compression level or size) without allocating a closure to capture
    /// it — <paramref name="factory"/> can be a <see langword="static"/> lambda, so nothing is
    /// allocated on the (common) path where the pool already has an entry to hand back.
    /// </summary>
    private static T RentOrCreate<TState, T>(Stack<T> pool, TState state, Func<TState, T> factory) => pool.TryPop(out var pooled) ? pooled : factory(state);

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

        // The decompressor is tracked alongside its task (rather than only inside the task's
        // result) so cleanup can dispose it even for a chunk whose decompression faulted — a
        // faulted task never produces a DecodedChunk to recover it from.
        private readonly Queue<(Task<DecodedChunk> Task, ZstdSharp.Decompressor Decompressor)> _pending = new();

        // Buffers and decompression contexts handed back once their chunk has been consumed, for
        // reuse by later chunks. Only ever touched on the reading thread; workers just receive a
        // buffer/context to fill/use, mirroring WriteStream's per-instance compressor pool rather
        // than a [ThreadStatic] context that would live (and hold native state) for the lifetime of
        // whichever thread-pool worker thread happened to run a chunk.
        private readonly Stack<byte[]> _freeCompressed = new();
        private readonly Stack<byte[]> _freeDecompressed = new();
        private readonly Stack<ZstdSharp.Decompressor> _freeDecompressors = new();
        private int _compressedCapacity;
        private int _decompressedCapacity;

        private byte[] _currentChunk = [];
        private int _currentChunkLength;
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
                    // Not gated on _endOfStream alone: that flag only means "no more chunk
                    // headers left to read," but chunks already prefetched into _pending (read
                    // ahead of the terminator) still need to be drained.
                    if (!TryAdvanceChunk())
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
            if (_positionInChunk == _currentChunkLength && !TryAdvanceChunk())
            {
                return -1;
            }

            return _currentChunk[_positionInChunk++];
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static DecodedChunk Decompress(ZstdSharp.Decompressor decompressor, byte[] compressed, int compressedLength, byte[]? destination, int decompressedLength)
        {
            if (destination is null)
            {
                var unwrapped = decompressor.Unwrap(compressed.AsSpan(0, compressedLength)).ToArray();
                return new(compressed, unwrapped, unwrapped.Length);
            }

            var written = decompressor.Unwrap(compressed.AsSpan(0, compressedLength), destination.AsSpan(0, decompressedLength));
            return new(compressed, destination, written);
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
            // A loop rather than recursing on an empty chunk: a crafted image can hold any number
            // of frames that decompress to nothing, and recursing once per frame would overflow
            // the stack.
            while (true)
            {
                // The current chunk has been fully consumed, so its buffer is free for a later chunk.
                // Zeroed before returning to the pool: it held this chunk's decompressed plaintext, and
                // an oversized buffer kept around from an earlier, larger chunk would otherwise leave
                // that plaintext resident beyond the current chunk's own length.
                if (_currentChunk.Length > 0)
                {
                    SecureZero.All(_currentChunk);
                    _freeDecompressed.Push(_currentChunk);
                    _currentChunk = [];
                    _currentChunkLength = 0;
                    _positionInChunk = 0;
                }

                FillPending();

                if (_pending.Count == 0)
                {
                    _endOfStream = true;
                    return false;
                }

                var (task, decompressor) = _pending.Dequeue();
                DecodedChunk decoded;
                try
                {
                    decoded = task.GetAwaiter().GetResult();
                }
                catch
                {
                    // The task never produced a DecodedChunk to recover the decompressor from, so
                    // it has to be disposed directly here instead of via the usual pool-push below.
                    decompressor.Dispose();
                    throw;
                }

                // Zeroed like _currentChunk below: for an encrypted image this buffer held the
                // already-decrypted, still-Zstd-compressed bytes of this chunk's plaintext content, so
                // it shouldn't linger in the pool unscrubbed any more than the decompressed form does.
                SecureZero.All(decoded.Compressed);
                _freeCompressed.Push(decoded.Compressed);
                _freeDecompressors.Push(decompressor);
                _currentChunk = decoded.Decompressed;
                _currentChunkLength = decoded.Length;
                _positionInChunk = 0;

                // Immediately queue the next chunk so decompression of what's now the tail of the
                // pending queue overlaps with the caller consuming _currentChunk.
                FillPending();

                if (_currentChunkLength > 0)
                {
                    return true;
                }
            }
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

            if (length < 0)
            {
                throw new InvalidDataException($"Invalid compressed chunk length: {length}.");
            }

            var compressed = RentBuffer(_freeCompressed, length, ref _compressedCapacity);
            source.ReadExactly(compressed.AsSpan(0, length));

            // Sized from the frame header here (a cheap scan) so the destination can come out of
            // the reader-thread-only pool, and so the worker decompresses straight into it instead
            // of into a fresh array that would then be copied.
            var decompressedSize = ZstdSharp.Decompressor.GetDecompressedSize(compressed.AsSpan(0, length));
            if (decompressedSize > (ulong)Array.MaxLength)
            {
                throw new InvalidDataException($"Compressed chunk claims an oversized payload ({decompressedSize:N0} bytes).");
            }

            var decompressedLength = (int)decompressedSize;

            // A zero size means the frame header carries no content size (nothing this writer
            // produces, but a foreign payload may): there's no way to size a destination up front,
            // so the worker allocates one itself.
            var destination = decompressedLength == 0
                ? null
                : RentBuffer(_freeDecompressed, decompressedLength, ref _decompressedCapacity);
            var decompressor = RentOrCreate(_freeDecompressors, static () => new ZstdSharp.Decompressor());

            Task<DecodedChunk> task;
            try
            {
                // With no parallelism to gain, decompress inline rather than hop to the thread
                // pool and block on it. Besides saving the hop, this is what makes it safe to read
                // from inside a thread-pool work item (as a parallel segment load does) without
                // blocking that worker on another work item queued behind it.
                task = _maxDegreeOfParallelism == 1
                    ? Task.FromResult(Decompress(decompressor, compressed, length, destination, decompressedLength))
                    : Task.Run(() => Decompress(decompressor, compressed, length, destination, decompressedLength));
            }
            catch
            {
                // The parallelism == 1 branch above runs Decompress() eagerly; if it throws, no
                // task was ever created to carry decompressor to _pending, so it has to be
                // disposed here instead of by the usual TryAdvanceChunk/Dispose cleanup paths.
                decompressor.Dispose();
                throw;
            }

            _pending.Enqueue((task, decompressor));
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // The chunk currently buffered for reading is only zeroed here if the stream is
                // disposed before it's fully consumed — TryAdvanceChunk zeroes it once Read/ReadByte
                // has drained it, which never happens for whatever chunk is live at early disposal.
                SecureZero.All(_currentChunk);

                // Normal completion already drained _pending (TryAdvanceChunk only reports
                // end-of-stream once it's empty), so this only has anything to do when the stream
                // is disposed early (e.g. an exception partway through reading). The decompressor
                // is disposed unconditionally, outside the try/catch: it was rented/created before
                // the task ran (in TryQueueNextChunk), so it's a live object to release even for a
                // chunk whose decompression faulted, not just for one that produced a result.
                while (_pending.TryDequeue(out var pending))
                {
                    try
                    {
                        // Zeroed rather than pooled: this chunk's plaintext was never handed to a
                        // caller (the stream is being disposed early), but it still shouldn't
                        // linger in memory any longer than a chunk that was actually read.
                        ZeroDecodedChunk(pending.Task.GetAwaiter().GetResult());
                    }
                    catch
                    {
                        // The chunk's own failure is what the caller already observed (this stream
                        // is being disposed after that propagated) or will observe shortly.
                    }

                    pending.Decompressor.Dispose();
                }

                while (_freeDecompressors.TryPop(out var decompressor))
                {
                    decompressor.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Zeroes both buffers of a decoded chunk that will never be handed to a caller (this
        /// stream is being disposed before the chunk was read) — used only from the early-disposal
        /// path above; the normal read path in <see cref="TryAdvanceChunk"/> zeroes each buffer
        /// separately since one of them (<c>Decompressed</c>) becomes <see cref="_currentChunk"/>
        /// and is still live for the caller at that point.
        /// </summary>
        private static void ZeroDecodedChunk(DecodedChunk decoded)
        {
            SecureZero.All(decoded.Decompressed);
            SecureZero.All(decoded.Compressed);
        }

        private readonly record struct DecodedChunk(byte[] Compressed, byte[] Decompressed, int Length);
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
    internal sealed class WriteStream(Stream target, int level, int? maxDegreeOfParallelism = null, int? chunkSize = null) : Stream
    {
        private readonly int _maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism ?? Environment.ProcessorCount);
        private readonly int _chunkSize = chunkSize ?? ChunkSize;

        // The compressor and buffers are tracked alongside their task (rather than only inside the
        // task's result) so DrainOne can recover and dispose/scrub them even for a chunk whose
        // compression faulted — a faulted task never produces an EncodedChunk to recover them from.
        private readonly Queue<(Task<EncodedChunk> Task, ZstdSharp.Compressor Compressor, byte[] Input, byte[] Output)> _pending = new();

        // Input buffers, compress-bound output buffers and compression contexts are recycled once
        // their chunk has been written out, instead of allocating a fresh (large-object-heap) input
        // array, an output array plus an exact-size copy of it, and a new context for every chunk.
        // Each pool holds at most one entry per chunk in flight, and is only ever touched on the
        // writing thread; workers just receive what to use.
        private readonly Stack<byte[]> _freeInputs = new();
        private readonly Stack<byte[]> _freeOutputs = new();
        private readonly Stack<ZstdSharp.Compressor> _freeCompressors = new();

        private byte[] _buffer = [];
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
        /// chunks to be compressed and written, and writes the zero-length terminator.
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

            while (_freeCompressors.TryPop(out var compressor))
            {
                compressor.Dispose();
            }

            _freeInputs.Clear();
            _freeOutputs.Clear();
            _buffer = [];
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
                if (_buffer.Length == 0)
                {
                    _buffer = RentOrCreate(_freeInputs, _chunkSize, static size => new byte[size]);
                }

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

        private static EncodedChunk Compress(ZstdSharp.Compressor compressor, byte[] input, int length, byte[] output)
        {
            var written = compressor.Wrap(input.AsSpan(0, length), output);
            return new(written);
        }

        private void DrainOne()
        {
            var (task, compressor, input, output) = _pending.Dequeue();
            int length;
            try
            {
                length = task.GetAwaiter().GetResult().Length;
            }
            catch
            {
                // The task never produced an EncodedChunk to recover the compressor/buffers from.
                // input still holds this chunk's plaintext regardless of whether compression
                // itself succeeded, so it's scrubbed here the same as on the success path below;
                // output is dropped rather than pooled since nothing ever read it.
                SecureZero.All(input);
                compressor.Dispose();
                throw;
            }

            WriteChunkHeader(length);
            target.Write(output, 0, length);

            // The input buffer held this chunk's plaintext; zeroed before returning it to the pool
            // so a later, shorter chunk doesn't leave stale plaintext resident beyond its own
            // length (the same reasoning as ChunkedGcm.WriteStream.FlushChunk's ZeroMemory call).
            SecureZero.All(input);

            _freeCompressors.Push(compressor);
            _freeInputs.Push(input);
            _freeOutputs.Push(output);
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
            _buffer = [];
            _bufferLength = 0;

            var compressor = RentOrCreate(_freeCompressors, level, static lvl => new ZstdSharp.Compressor(lvl));
            var output = RentOrCreate(_freeOutputs, _chunkSize, static size => new byte[ZstdSharp.Compressor.GetCompressBound(size)]);

            var task = Task.Run(() => Compress(compressor, chunk, length, output));
            _pending.Enqueue((task, compressor, chunk, output));
        }

        private void WriteChunkHeader(int length)
        {
            Span<byte> lengthBytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, length);
            target.Write(lengthBytes);
        }

        private readonly record struct EncodedChunk(int Length);
    }
}
