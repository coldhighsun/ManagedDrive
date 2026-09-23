using System.Security.Cryptography;

namespace ManagedDrive.Core.Persistence;

/// <summary>
/// Shared helper for scrubbing plaintext out of buffers before they're pooled, recycled, or
/// dropped. Used by <see cref="ChunkedGcm"/>, <see cref="ParallelZstd"/>, and
/// <see cref="DiskImageSerializer"/> wherever a buffer that held decrypted/decompressed plaintext
/// is about to be reused or discarded, so a later, shorter use of the same (possibly pooled,
/// possibly oversized) buffer never leaves an earlier use's plaintext resident beyond its own
/// length.
/// </summary>
internal static class SecureZero
{
    /// <summary>
    /// Zeroes <paramref name="buffer"/> from <paramref name="offset"/> through its end — the
    /// unused tail left behind when a buffer's live length is less than a larger, reused buffer's
    /// capacity.
    /// </summary>
    internal static void From(byte[] buffer, int offset) => CryptographicOperations.ZeroMemory(buffer.AsSpan(offset));

    /// <summary>
    /// Zeroes the whole of <paramref name="buffer"/> — e.g. one holding an exact-size payload, or
    /// one being dropped outright. Equivalent to <c>From(buffer, 0)</c>, named separately so a
    /// whole-buffer zero reads as intentional rather than as an offset of <c>0</c> that could be
    /// mistaken for a placeholder.
    /// </summary>
    internal static void All(byte[] buffer) => From(buffer, 0);

    /// <summary>
    /// Zeroes exactly <paramref name="length"/> bytes of <paramref name="buffer"/> starting at
    /// <paramref name="offset"/>. For a buffer whose tail beyond its live length is already zeroed
    /// by a previous <see cref="From"/> call (rather than zeroed fresh on every use), this avoids
    /// re-zeroing that already-zero tail.
    /// </summary>
    internal static void Range(byte[] buffer, int offset, int length) => CryptographicOperations.ZeroMemory(buffer.AsSpan(offset, length));
}
