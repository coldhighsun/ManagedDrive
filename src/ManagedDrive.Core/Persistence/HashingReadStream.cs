using System.Security.Cryptography;

namespace ManagedDrive.Core.Persistence;

/// <summary>
/// Read-only pass-through stream that feeds every byte read through it into a SHA-256 hash, so the
/// consumer can verify the total content it parsed against a stored digest without buffering it.
/// </summary>
/// <param name="inner">The stream to read from; not disposed by this wrapper.</param>
internal sealed class HashingReadStream(Stream inner) : Stream
{
    /// <summary>
    /// Running hash of the bytes read so far.
    /// </summary>
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        if (read > 0)
        {
            _hash.AppendData(buffer[..read]);
        }

        return read;
    }

    /// <summary>
    /// Gets the number of bytes consumed by <see cref="DrainAndGetHash"/>, i.e. bytes that were
    /// left unread by the caller before draining.
    /// </summary>
    public long DrainedByteCount { get; private set; }

    /// <summary>
    /// Reads and hashes whatever is left in the inner stream, then returns the digest of
    /// everything that passed through this wrapper.
    /// </summary>
    /// <returns>
    /// The SHA-256 digest of all bytes read through this stream, including the drained remainder.
    /// </returns>
    public byte[] DrainAndGetHash()
    {
        Span<byte> scratch = stackalloc byte[4096];
        int read;
        while ((read = Read(scratch)) > 0)
        {
            DrainedByteCount += read;
        }

        return _hash.GetHashAndReset();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
