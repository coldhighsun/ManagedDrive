using System.Runtime.InteropServices;

namespace ManagedDrive.Benchmarks;

/// <summary>
/// Helpers that read a file with the Windows <c>FILE_FLAG_NO_BUFFERING</c> flag so the read
/// bypasses the OS unified page cache and hits the underlying medium directly. This is what
/// makes a "real SSD" read comparison honest: a plain read of a just-written file is served
/// from the kernel-side page cache (RAM), not the disk, which is why an uncached variant is
/// needed alongside it.
/// </summary>
/// <remarks>
/// <para>
/// NO_BUFFERING imposes three constraints, all satisfied by the benchmark's 4 KB / 1 MB sizes:
/// the file offset, the read length, and the buffer's memory address must each be a multiple of
/// the volume sector size. The buffer is therefore allocated with <see cref="NativeMemory.AlignedAlloc"/>.
/// </para>
/// </remarks>
internal static class UnbufferedIo
{
    /// <summary>
    /// The <c>FILE_FLAG_NO_BUFFERING</c> value, absent from the <see cref="FileOptions"/> enum.
    /// </summary>
    private const int FileFlagNoBuffering = 0x20000000;

    /// <summary>
    /// Sector alignment for the buffer, offsets, and lengths. 4 KB is a multiple of every common
    /// physical/logical sector size (512 B / 4 KB), so it is safe on all target volumes.
    /// </summary>
    private const nuint Alignment = 4096;

    /// <summary>
    /// Reads <paramref name="length"/> bytes from the start of <paramref name="path"/> with the OS
    /// page cache bypassed. <paramref name="length"/> must be a multiple of the sector size.
    /// </summary>
    public static unsafe void ReadFull(string path, int length)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, (FileOptions)FileFlagNoBuffering);

        var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)length, Alignment);
        try
        {
            var span = new Span<byte>(buffer, length);
            RandomAccess.Read(handle, span, fileOffset: 0);
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    /// <summary>
    /// Reads <paramref name="blockBytes"/> at each offset in <paramref name="alignedOffsets"/> from
    /// <paramref name="path"/> with the OS page cache bypassed. Every offset and
    /// <paramref name="blockBytes"/> must be a multiple of the sector size.
    /// </summary>
    public static unsafe void ReadBlocksAt(string path, long[] alignedOffsets, int blockBytes)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, (FileOptions)FileFlagNoBuffering);

        var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)blockBytes, Alignment);
        try
        {
            var span = new Span<byte>(buffer, blockBytes);
            foreach (var offset in alignedOffsets)
            {
                RandomAccess.Read(handle, span, offset);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }
}
