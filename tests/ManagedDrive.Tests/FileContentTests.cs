using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ManagedDrive.Tests;

public class FileContentTests
{
    [Fact]
    public void FromSpan_ThenToArray_RoundTripsLeadingBytes()
    {
        var content = FileContent.FromSpan([1, 2, 3, 4, 5], 512);

        Assert.Equal(512, content.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, content.ToArray(5));
    }

    [Fact]
    public void FromSpan_ZeroesBytesBeyondData()
    {
        var content = FileContent.FromSpan([9, 9, 9], 512);

        var tail = content.ToArray(512);
        Assert.Equal(9, tail[0]);
        Assert.All(tail[3..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void SmallFile_DoesNotAllocateFullChunk(int alignedLength)
    {
        var content = FileContent.CreateZeroed((ulong)alignedLength);

        // A small file must not pay a whole 64 KiB chunk: the terminal chunk is right-sized.
        Assert.Equal(alignedLength, content.BackingByteCount);
    }

    [Fact]
    public void MultiChunkFile_AllocatesFullChunksPlusRightSizedTail()
    {
        var content = FileContent.CreateZeroed((ulong)(FileContent.ChunkSize + 512));

        // One full 64 KiB chunk + a 512-byte tail, not two full chunks.
        Assert.Equal(FileContent.ChunkSize + 512, content.BackingByteCount);
    }

    [Fact]
    public void GrowAcrossChunkBoundary_PromotesTailToFullChunk()
    {
        var content = FileContent.CreateZeroed(4096);
        Assert.Equal(4096, content.BackingByteCount);

        content.Resize((ulong)(FileContent.ChunkSize + 512));

        // The former 4 KiB tail is promoted to a full chunk; new tail is 512 bytes.
        Assert.Equal(FileContent.ChunkSize + 512, content.BackingByteCount);
    }

    [Fact]
    public void WriteFrom_ReadTo_RoundTripsAcrossChunkBoundary()
    {
        var length = FileContent.ChunkSize * 3;
        var content = FileContent.CreateZeroed((ulong)length);

        // A pattern straddling the first chunk boundary.
        var pattern = new byte[400];
        for (var i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i % 251 + 1);
        }

        var offset = FileContent.ChunkSize - 150; // spans chunk 0 -> chunk 1
        WriteBytes(content, offset, pattern);

        Assert.Equal(pattern, ReadBytes(content, offset, pattern.Length));
    }

    [Fact]
    public void WriteFrom_ReadTo_RoundTripsSpanningManyChunks()
    {
        var length = FileContent.ChunkSize * 4;
        var content = FileContent.CreateZeroed((ulong)length);

        var pattern = new byte[FileContent.ChunkSize * 2 + 77];
        RandomNumberGenerator.Fill(pattern);

        WriteBytes(content, 100, pattern);

        Assert.Equal(pattern, ReadBytes(content, 100, pattern.Length));
    }

    [Fact]
    public void Resize_Grow_ExposesZeros()
    {
        var content = FileContent.FromSpan([1, 2, 3], 512);

        content.Resize(4096);

        Assert.Equal(4096, content.Length);
        Assert.All(ReadBytes(content, 512, 4096 - 512), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Resize_ShrinkThenGrow_DoesNotLeakStaleData()
    {
        // Fill the first two chunks with non-zero data, shrink into chunk 0, then grow back and
        // confirm the re-exposed region reads as zero rather than the old data.
        var content = FileContent.CreateZeroed((ulong)(FileContent.ChunkSize * 2));
        var filler = new byte[FileContent.ChunkSize * 2];
        Array.Fill(filler, (byte)0xAB);
        WriteBytes(content, 0, filler);

        content.Resize(1024);
        content.Resize((ulong)(FileContent.ChunkSize * 2));

        // Bytes past the retained 1024 must be zero, not 0xAB.
        var exposed = ReadBytes(content, 1024, FileContent.ChunkSize);
        Assert.All(exposed, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Clone_ProducesIndependentContent()
    {
        var original = FileContent.FromSpan([1, 2, 3], 512);
        var clone = original.Clone();

        WriteBytes(original, 0, [9, 9, 9]);

        Assert.Equal(new byte[] { 1, 2, 3 }, clone.ToArray(3));
    }

    [Fact]
    public void CopyTo_WritesLeadingBytes()
    {
        var content = FileContent.FromSpan([1, 2, 3, 4], 512);
        using var ms = new MemoryStream();

        content.CopyTo(ms, 4);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ms.ToArray());
    }

    [Fact]
    public void HashInto_MatchesSha256OfLeadingBytes()
    {
        var data = new byte[FileContent.ChunkSize + 500];
        RandomNumberGenerator.Fill(data);
        var content = FileContent.FromSpan(data, FileNode.AlignToAllocationUnit((ulong)data.Length));

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        content.HashInto(incremental, data.Length);

        Assert.Equal(SHA256.HashData(data), incremental.GetHashAndReset());
    }

    [Fact]
    public void FillFromStream_ShortStream_LeavesRemainderZero()
    {
        var content = FileContent.CreateZeroed(512);
        using var source = new MemoryStream([1, 2, 3]);

        content.FillFromStream(source, 10);

        var bytes = content.ToArray(10);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 0, 0, 0, 0, 0 }, bytes);
    }

    [Fact]
    public void AsReadOnlyStream_ReadsLeadingBytesAcrossChunks()
    {
        var data = new byte[FileContent.ChunkSize + 123];
        RandomNumberGenerator.Fill(data);
        var content = FileContent.FromSpan(data, FileNode.AlignToAllocationUnit((ulong)data.Length));

        using var stream = content.AsReadOnlyStream(data.Length);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);

        Assert.Equal(data, copy.ToArray());
    }

    private static void WriteBytes(FileContent content, long offset, byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
            content.WriteFrom(ptr, (ulong)offset, (uint)data.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static byte[] ReadBytes(FileContent content, long offset, int length)
    {
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            content.ReadTo((ulong)offset, ptr, (uint)length);
            var buffer = new byte[length];
            Marshal.Copy(ptr, buffer, 0, length);
            return buffer;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
