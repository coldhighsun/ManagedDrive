using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ManagedDrive.Core.FileSystem;

/// <summary>
/// Growable in-memory backing store for a single file's content, implemented as a list of
/// fixed-size chunks rather than one contiguous <c>byte[]</c>.
/// <para>
/// This layout has two purposes. First, growing a file appends chunks instead of reallocating
/// and copying the whole buffer, so a streaming write no longer degrades to O(n²) copying.
/// Second, every chunk is <see cref="ChunkSize"/> bytes — deliberately below the 85 000-byte
/// large-object-heap threshold — so even a multi-gigabyte file never places a single large
/// object on the LOH, avoiding LOH fragmentation and the associated GC pressure.
/// </para>
/// <para>
/// Chunks are plain <c>new byte[]</c> allocations (not pooled). Pooling via
/// <c>ArrayPool&lt;byte&gt;</c> would trade GC work for buffer-lifetime hazards (double-return,
/// use-after-return) and would require explicitly zeroing rented memory to avoid leaking a
/// previous tenant's bytes to callers. Plain allocations sidestep all of that while still
/// meeting both goals above, since <see cref="ChunkSize"/>-sized arrays stay in gen0.
/// </para>
/// <para>
/// Instances are not internally synchronized. Like the previous raw <c>byte[]</c>, content is
/// mutated only from WinFsp callbacks, which the driver serializes per file.
/// </para>
/// </summary>
public sealed class FileContent
{
    /// <summary>
    /// Size of each backing chunk, in bytes. Kept just under the 85 000-byte large-object-heap
    /// threshold so no chunk is ever allocated on the LOH.
    /// </summary>
    public const int ChunkSize = 64 * 1024;

    // Invariant: every chunk except the last is exactly ChunkSize bytes. The last chunk is
    // right-sized (a power of two, capped at ChunkSize) to just cover the file's remaining bytes,
    // so a small file doesn't pay a full 64 KiB chunk. Only its capacity may exceed its used
    // portion; a shrink leaves the last chunk's capacity in place rather than reallocating it.
    private readonly List<byte[]> _chunks = [];

    private long _length;

    private FileContent()
    {
    }

    /// <summary>
    /// Gets the logical length of the content in bytes. This mirrors the node's aligned
    /// <see cref="Fsp.Interop.FileInfo.AllocationSize"/>; the valid data range is a prefix of it.
    /// </summary>
    public long Length => _length;

    /// <summary>
    /// Gets the total number of bytes actually allocated across all chunks. Test/diagnostic hook
    /// used to guard against over-allocation of small files.
    /// </summary>
    internal long BackingByteCount
    {
        get
        {
            long total = 0;
            foreach (var chunk in _chunks)
            {
                total += chunk.Length;
            }

            return total;
        }
    }

    /// <summary>
    /// Creates a zero-filled content buffer of the given (already allocation-aligned) length.
    /// </summary>
    /// <param name="alignedLength">The logical length in bytes.</param>
    /// <returns>
    /// A new <see cref="FileContent"/> whose entire range reads as zero.
    /// </returns>
    public static FileContent CreateZeroed(ulong alignedLength)
    {
        var content = new FileContent();
        content.Resize(alignedLength);
        return content;
    }

    /// <summary>
    /// Creates a content buffer of <paramref name="alignedLength"/> bytes whose leading
    /// <paramref name="data"/>.Length bytes are copied from <paramref name="data"/> and whose
    /// remaining bytes are zero.
    /// </summary>
    /// <param name="data">The leading bytes to copy in. Must not exceed <paramref name="alignedLength"/>.</param>
    /// <param name="alignedLength">The logical length in bytes.</param>
    /// <returns>
    /// A new <see cref="FileContent"/> pre-filled with <paramref name="data"/>.
    /// </returns>
    public static FileContent FromSpan(ReadOnlySpan<byte> data, ulong alignedLength)
    {
        var content = CreateZeroed(alignedLength);
        content.WriteManaged(0, data);
        return content;
    }

    /// <summary>
    /// Resizes the content to <paramref name="alignedLength"/> bytes. Growing exposes zero-filled
    /// bytes in the range <c>[oldLength, newLength)</c> (regardless of any data left behind by an
    /// earlier shrink); shrinking discards trailing chunks.
    /// </summary>
    /// <param name="alignedLength">The new logical length in bytes.</param>
    public void Resize(ulong alignedLength)
    {
        var newLength = (long)alignedLength;
        if (newLength == _length)
        {
            return;
        }

        if (newLength == 0)
        {
            _chunks.Clear();
            _length = 0;
            return;
        }

        var oldLength = _length;
        var oldChunkCount = _chunks.Count;
        var oldTerminalCapacity = oldChunkCount > 0 ? _chunks[oldChunkCount - 1].Length : 0;
        var neededChunks = ChunkCountFor(newLength);

        if (newLength > oldLength)
        {
            // If the old last chunk was right-sized (partial) and will no longer be the terminal
            // chunk, promote it to a full ChunkSize chunk first.
            if (oldChunkCount > 0 && neededChunks > oldChunkCount && _chunks[oldChunkCount - 1].Length < ChunkSize)
            {
                var old = _chunks[oldChunkCount - 1];
                var promoted = new byte[ChunkSize];
                Buffer.BlockCopy(old, 0, promoted, 0, old.Length);
                _chunks[oldChunkCount - 1] = promoted;
            }

            // Append full-size chunks for every position except the terminal one.
            while (_chunks.Count < neededChunks - 1)
            {
                _chunks.Add(new byte[ChunkSize]);
            }

            // Size (or grow) the terminal chunk to cover its portion of the new length.
            var terminalUsed = newLength - (long)(neededChunks - 1) * ChunkSize;
            var wantCapacity = TerminalCapacity(terminalUsed);
            if (_chunks.Count == neededChunks)
            {
                var terminal = _chunks[neededChunks - 1];
                if (terminal.Length < wantCapacity)
                {
                    var bigger = new byte[wantCapacity];
                    Buffer.BlockCopy(terminal, 0, bigger, 0, terminal.Length);
                    _chunks[neededChunks - 1] = bigger;
                }
            }
            else
            {
                _chunks.Add(new byte[wantCapacity]);
            }

            // Only bytes that fall inside capacity that existed before this grow can hold stale
            // data (from an earlier shrink, or a promoted chunk's former slack); everything past
            // that boundary sits in freshly allocated (already-zero) arrays. Zero just that slice.
            var preExistingCapacity = oldChunkCount > 0
                ? (long)(oldChunkCount - 1) * ChunkSize + oldTerminalCapacity
                : 0;
            var staleEnd = Math.Min(newLength, preExistingCapacity);
            if (staleEnd > oldLength)
            {
                ZeroRange(oldLength, staleEnd - oldLength);
            }
        }
        else
        {
            // Shrink: drop trailing chunks. The new terminal chunk keeps whatever capacity it
            // already has (>= its used portion); we never grow on a shrink.
            while (_chunks.Count > neededChunks)
            {
                _chunks.RemoveAt(_chunks.Count - 1);
            }
        }

        _length = newLength;
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes starting at <paramref name="offset"/> into the
    /// native buffer <paramref name="destination"/>.
    /// </summary>
    /// <param name="offset">Zero-based byte offset into the content.</param>
    /// <param name="destination">Destination native buffer.</param>
    /// <param name="length">Number of bytes to copy.</param>
    public void ReadTo(ulong offset, IntPtr destination, uint length)
    {
        var pos = (long)offset;
        var destOffset = 0;
        var remaining = (long)length;

        while (remaining > 0)
        {
            var chunkIndex = (int)(pos / ChunkSize);
            var chunkOffset = (int)(pos % ChunkSize);
            var n = (int)Math.Min(remaining, ChunkSize - chunkOffset);

            Marshal.Copy(_chunks[chunkIndex], chunkOffset, IntPtr.Add(destination, destOffset), n);

            pos += n;
            destOffset += n;
            remaining -= n;
        }
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes from the native buffer <paramref name="source"/>
    /// into the content starting at <paramref name="offset"/>. The target range must already be
    /// within <see cref="Length"/>.
    /// </summary>
    /// <param name="source">Source native buffer.</param>
    /// <param name="offset">Zero-based byte offset into the content.</param>
    /// <param name="length">Number of bytes to copy.</param>
    public void WriteFrom(IntPtr source, ulong offset, uint length)
    {
        var pos = (long)offset;
        var srcOffset = 0;
        var remaining = (long)length;

        while (remaining > 0)
        {
            var chunkIndex = (int)(pos / ChunkSize);
            var chunkOffset = (int)(pos % ChunkSize);
            var n = (int)Math.Min(remaining, ChunkSize - chunkOffset);

            Marshal.Copy(IntPtr.Add(source, srcOffset), _chunks[chunkIndex], chunkOffset, n);

            pos += n;
            srcOffset += n;
            remaining -= n;
        }
    }

    /// <summary>
    /// Writes the first <paramref name="count"/> bytes of the content to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The stream to write to.</param>
    /// <param name="count">Number of leading bytes to write.</param>
    public void CopyTo(Stream destination, long count)
    {
        var remaining = count;
        var chunkIndex = 0;

        while (remaining > 0)
        {
            var n = (int)Math.Min(remaining, ChunkSize);
            destination.Write(_chunks[chunkIndex], 0, n);
            remaining -= n;
            chunkIndex++;
        }
    }

    /// <summary>
    /// Reads up to <paramref name="count"/> bytes from <paramref name="source"/> into the content
    /// starting at offset 0. The content must already be at least <paramref name="count"/> bytes.
    /// If the stream ends early, the remaining bytes keep their current (zero) value — mirroring a
    /// bounded copy rather than throwing.
    /// </summary>
    /// <param name="source">The stream to read from.</param>
    /// <param name="count">Maximum number of bytes to read.</param>
    public void FillFromStream(Stream source, long count)
    {
        var remaining = count;
        var chunkIndex = 0;

        while (remaining > 0)
        {
            var segment = (int)Math.Min(remaining, ChunkSize);
            var filled = 0;

            while (filled < segment)
            {
                var read = source.Read(_chunks[chunkIndex], filled, segment - filled);
                if (read == 0)
                {
                    return;
                }

                filled += read;
            }

            remaining -= segment;
            chunkIndex++;
        }
    }

    /// <summary>
    /// Feeds the first <paramref name="count"/> bytes of the content into an incremental hash,
    /// without materializing a contiguous copy.
    /// </summary>
    /// <param name="hash">The incremental hash to append to.</param>
    /// <param name="count">Number of leading bytes to hash.</param>
    public void HashInto(IncrementalHash hash, long count)
    {
        var remaining = count;
        var chunkIndex = 0;

        while (remaining > 0)
        {
            var n = (int)Math.Min(remaining, ChunkSize);
            hash.AppendData(_chunks[chunkIndex].AsSpan(0, n));
            remaining -= n;
            chunkIndex++;
        }
    }

    /// <summary>
    /// Returns a read-only, seekable stream over the first <paramref name="count"/> bytes of the
    /// content. The stream reads directly from the chunks without copying them.
    /// </summary>
    /// <param name="count">Number of leading bytes the stream should expose.</param>
    /// <returns>
    /// A read-only <see cref="Stream"/> positioned at 0.
    /// </returns>
    public Stream AsReadOnlyStream(long count) => new ChunkReadStream(this, count);

    /// <summary>
    /// Materializes the first <paramref name="count"/> bytes into a contiguous array. Prefer the
    /// streaming members where possible; this allocates a single (potentially large) array.
    /// </summary>
    /// <param name="count">Number of leading bytes to copy out.</param>
    /// <returns>
    /// A new array holding the requested bytes.
    /// </returns>
    public byte[] ToArray(long count)
    {
        var result = new byte[count];
        var remaining = count;
        var chunkIndex = 0;
        var destOffset = 0;

        while (remaining > 0)
        {
            var n = (int)Math.Min(remaining, ChunkSize);
            Buffer.BlockCopy(_chunks[chunkIndex], 0, result, destOffset, n);
            remaining -= n;
            destOffset += n;
            chunkIndex++;
        }

        return result;
    }

    /// <summary>
    /// Returns a deep, independent copy of this content.
    /// </summary>
    /// <returns>
    /// A new <see cref="FileContent"/> whose chunks are copies of this instance's chunks.
    /// </returns>
    public FileContent Clone()
    {
        var clone = CreateZeroed((ulong)_length);

        var remaining = _length;
        var chunkIndex = 0;
        while (remaining > 0)
        {
            // Copy only the used portion of each chunk; capacities may differ between the two
            // instances (a shrink can leave a larger terminal capacity behind), but the logical
            // bytes match.
            var n = (int)Math.Min(remaining, ChunkSize);
            Buffer.BlockCopy(_chunks[chunkIndex], 0, clone._chunks[chunkIndex], 0, n);
            remaining -= n;
            chunkIndex++;
        }

        return clone;
    }

    private static int ChunkCountFor(long length) => (int)((length + ChunkSize - 1) / ChunkSize);

    /// <summary>
    /// Computes the backing-array capacity for a terminal chunk holding <paramref name="usedBytes"/>
    /// logical bytes: the smallest power of two that covers it, clamped to
    /// [512, <see cref="ChunkSize"/>]. Power-of-two growth keeps repeated small extensions of the
    /// last chunk amortized-linear rather than quadratic.
    /// </summary>
    private static int TerminalCapacity(long usedBytes)
    {
        if (usedBytes >= ChunkSize)
        {
            return ChunkSize;
        }

        var capacity = 512L;
        while (capacity < usedBytes)
        {
            capacity <<= 1;
        }

        return (int)capacity;
    }

    private void WriteManaged(long offset, ReadOnlySpan<byte> data)
    {
        var pos = offset;
        var srcOffset = 0;
        var remaining = data.Length;

        while (remaining > 0)
        {
            var chunkIndex = (int)(pos / ChunkSize);
            var chunkOffset = (int)(pos % ChunkSize);
            var n = Math.Min(remaining, ChunkSize - chunkOffset);

            data.Slice(srcOffset, n).CopyTo(_chunks[chunkIndex].AsSpan(chunkOffset, n));

            pos += n;
            srcOffset += n;
            remaining -= n;
        }
    }

    private void ZeroRange(long start, long count)
    {
        var pos = start;
        var remaining = count;

        while (remaining > 0)
        {
            var chunkIndex = (int)(pos / ChunkSize);
            var chunkOffset = (int)(pos % ChunkSize);
            var n = (int)Math.Min(remaining, ChunkSize - chunkOffset);

            Array.Clear(_chunks[chunkIndex], chunkOffset, n);

            pos += n;
            remaining -= n;
        }
    }

    /// <summary>
    /// Read-only, seekable view over a <see cref="FileContent"/>'s leading bytes that reads
    /// straight from the chunk list, avoiding a contiguous copy.
    /// </summary>
    private sealed class ChunkReadStream(FileContent content, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = length - _position;
            if (available <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(count, available);
            var read = 0;

            while (read < toRead)
            {
                var chunkIndex = (int)(_position / ChunkSize);
                var chunkOffset = (int)(_position % ChunkSize);
                var n = Math.Min(toRead - read, ChunkSize - chunkOffset);

                Buffer.BlockCopy(content._chunks[chunkIndex], chunkOffset, buffer, offset + read, n);

                _position += n;
                read += n;
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => _position,
            };

            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
