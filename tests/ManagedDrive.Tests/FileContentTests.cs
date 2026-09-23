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
    [InlineData(FileContent.ChunkSize + 512)]
    public void CreateZeroed_DoesNotMaterializeAnyChunks(int alignedLength)
    {
        var content = FileContent.CreateZeroed((ulong)alignedLength);

        // Nothing has been written yet, so every chunk must stay sparse (null) until touched.
        Assert.Equal(0, content.BackingByteCount);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void WriteFrom_SmallFile_MaterializesOnlyRightSizedTailChunk(int alignedLength)
    {
        var content = FileContent.CreateZeroed((ulong)alignedLength);

        WriteBytes(content, 0, [1, 2, 3]);

        // Writing into the terminal chunk must not pay a whole 64 KiB chunk: it's right-sized.
        Assert.Equal(alignedLength, content.BackingByteCount);
    }

    [Fact]
    public void WriteFrom_MultiChunkFile_MaterializesOnlyTouchedChunk()
    {
        var content = FileContent.CreateZeroed(FileContent.ChunkSize * 4);

        WriteBytes(content, 0, [1, 2, 3]);

        // Only the first (touched) chunk materializes; the other three stay sparse.
        Assert.Equal(FileContent.ChunkSize, content.BackingByteCount);
    }

    [Fact]
    public void GrowAcrossChunkBoundary_PromotesWrittenTailToFullChunk_NewTailStaysSparse()
    {
        var content = FileContent.CreateZeroed(4096);
        WriteBytes(content, 0, [1, 2, 3]);
        Assert.Equal(4096, content.BackingByteCount);

        content.Resize(FileContent.ChunkSize + 512);

        // The written 4 KiB tail is promoted to a full chunk; the newly exposed 512-byte tail was
        // never written, so it stays sparse instead of materializing a second small array.
        Assert.Equal(FileContent.ChunkSize, content.BackingByteCount);
    }

    [Fact]
    public void GrowMaterializedTailPastFirstChunk_JumpsStraightToFullChunk()
    {
        var content = FileContent.CreateZeroed(FileContent.ChunkSize + 512);
        WriteBytes(content, FileContent.ChunkSize, [1, 2, 3]);

        // A fresh tail chunk is still right-sized to its first write.
        Assert.Equal(512, content.BackingByteCount);

        content.Resize(FileContent.ChunkSize + 1024);

        // Growing it again means the file is being appended to: go straight to a full chunk
        // instead of doubling (512 -> 1024 -> ... -> 64 KiB) and copying at every step.
        Assert.Equal(FileContent.ChunkSize, content.BackingByteCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, ReadBytes(content, FileContent.ChunkSize, 3));
    }

    [Fact]
    public void GrowMaterializedFirstChunk_StillDoublesCapacity()
    {
        var content = FileContent.CreateZeroed(512);
        WriteBytes(content, 0, [1, 2, 3]);

        content.Resize(1024);

        // A small single-chunk file keeps power-of-two growth so it doesn't pay a whole 64 KiB.
        Assert.Equal(1024, content.BackingByteCount);
    }

    [Fact]
    public void ReadTo_SparseRegion_ReturnsZeroWithoutMaterializing()
    {
        var content = FileContent.CreateZeroed(FileContent.ChunkSize * 2);

        var bytes = ReadBytes(content, 0, FileContent.ChunkSize * 2);

        Assert.All(bytes, b => Assert.Equal(0, b));
        Assert.Equal(0, content.BackingByteCount);
    }

    [Fact]
    public void Clone_PreservesSparsenessOfUnwrittenChunks()
    {
        var original = FileContent.CreateZeroed(FileContent.ChunkSize * 2);
        WriteBytes(original, 0, [1, 2, 3]);

        var clone = original.Clone();

        // Only the chunk the source actually wrote to should materialize in the clone.
        Assert.Equal(FileContent.ChunkSize, clone.BackingByteCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, ReadBytes(clone, 0, 3));
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
        var content = FileContent.CreateZeroed(FileContent.ChunkSize * 2);
        var filler = new byte[FileContent.ChunkSize * 2];
        Array.Fill(filler, (byte)0xAB);
        WriteBytes(content, 0, filler);

        content.Resize(1024);
        content.Resize(FileContent.ChunkSize * 2);

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

    // The members below are handed a byte count computed from the node's FileSize before they
    // take the content lock, so a concurrent truncation can leave the count past Length. They
    // must still produce exactly that many bytes (zero-padded) rather than throw, or a save
    // racing a truncate fails outright and the image's length prefix no longer matches its data.

    [Fact]
    public void CopyTo_CountBeyondShrunkLength_PadsWithZeros()
    {
        var content = FileContent.FromSpan(Filled(FileContent.ChunkSize * 2, 7), FileContent.ChunkSize * 2);
        content.Resize(512);
        using var ms = new MemoryStream();

        content.CopyTo(ms, FileContent.ChunkSize * 2);

        var expected = new byte[FileContent.ChunkSize * 2];
        Array.Fill(expected, (byte)7, 0, 512);
        Assert.Equal(expected, ms.ToArray());
    }

    [Fact]
    public void HashInto_CountBeyondShrunkLength_HashesZeroPaddedBytes()
    {
        var content = FileContent.FromSpan(Filled(FileContent.ChunkSize * 2, 7), FileContent.ChunkSize * 2);
        content.Resize(512);

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        content.HashInto(incremental, FileContent.ChunkSize * 2);

        var expected = new byte[FileContent.ChunkSize * 2];
        Array.Fill(expected, (byte)7, 0, 512);
        Assert.Equal(SHA256.HashData(expected), incremental.GetHashAndReset());
    }

    [Fact]
    public void AsReadOnlyStream_ContentShrunkMidRead_PadsWithZeros()
    {
        var content = FileContent.FromSpan(Filled(FileContent.ChunkSize * 2, 7), FileContent.ChunkSize * 2);
        using var stream = content.AsReadOnlyStream(FileContent.ChunkSize * 2);
        var buffer = new byte[FileContent.ChunkSize * 2];

        var first = stream.Read(buffer, 0, 1024);
        content.Resize(512);
        var rest = 0;
        int read;
        while ((read = stream.Read(buffer, first + rest, buffer.Length - first - rest)) > 0)
        {
            rest += read;
        }

        var expected = new byte[FileContent.ChunkSize * 2];
        Array.Fill(expected, (byte)7, 0, 1024);
        Assert.Equal(expected.Length, first + rest);
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void ToArray_CountBeyondShrunkLength_PadsWithZeros()
    {
        var content = FileContent.FromSpan(Filled(FileContent.ChunkSize * 2, 7), FileContent.ChunkSize * 2);
        content.Resize(512);

        var bytes = content.ToArray(FileContent.ChunkSize * 2);

        var expected = new byte[FileContent.ChunkSize * 2];
        Array.Fill(expected, (byte)7, 0, 512);
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void WriteCost_MatchesBytesTheWriteMaterializes()
    {
        var content = FileContent.CreateZeroed(FileContent.ChunkSize * 3 + 1024);
        var data = new byte[FileContent.ChunkSize + 100];

        var cost = content.WriteCost(FileContent.ChunkSize - 50, (uint)data.Length);
        WriteBytes(content, FileContent.ChunkSize - 50, data);

        Assert.Equal(content.BackingByteCount, cost);
        Assert.Equal(0, content.WriteCost(FileContent.ChunkSize - 50, (uint)data.Length));
    }

    [Fact]
    public void WriteCost_TerminalChunk_UsesRightSizedCapacity()
    {
        var content = FileContent.CreateZeroed(FileContent.ChunkSize + 1024);

        var cost = content.WriteCost(FileContent.ChunkSize, 10);
        WriteBytes(content, FileContent.ChunkSize, new byte[10]);

        Assert.Equal(1024, cost);
        Assert.Equal(1024, content.BackingByteCount);
    }

    [Fact]
    public void ResizeCost_SparseContent_IsZero()
    {
        var content = FileContent.CreateZeroed(512);

        Assert.Equal(0, content.ResizeCost(64UL * 1024 * 1024));
    }

    [Fact]
    public void ResizeCost_GrowingWrittenTerminalChunk_MatchesNewCapacity()
    {
        var content = FileContent.FromSpan(new byte[100], 512);

        var cost = content.ResizeCost(4096);
        content.Resize(4096);

        Assert.Equal(4096, cost);
        Assert.Equal(4096, content.BackingByteCount);
    }

    [Fact]
    public void ResizeCost_PromotingWrittenTerminalChunk_CountsFullChunk()
    {
        var content = FileContent.FromSpan(new byte[100], 512);

        Assert.Equal(FileContent.ChunkSize, content.ResizeCost(FileContent.ChunkSize * 2));
        Assert.Equal(0, content.ResizeCost(256));
    }

    private static byte[] Filled(int length, byte value)
    {
        var data = new byte[length];
        Array.Fill(data, value);
        return data;
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
