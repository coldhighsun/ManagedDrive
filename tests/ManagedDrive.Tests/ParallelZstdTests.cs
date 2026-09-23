namespace ManagedDrive.Tests;

public sealed class ParallelZstdTests
{
    [Theory]
    [InlineData(0, 64)]
    [InlineData(1, 64)]
    [InlineData(64, 64)]
    [InlineData(1000, 64)]
    [InlineData(5000, 4096)]
    public void CompressFramed_MatchesWriteStreamOutputAndRoundTrips(int length, int chunkSize)
    {
        // Extra trailing capacity beyond length must be ignored, as with a MemoryStream buffer.
        var data = new byte[length + 17];
        new Random(length).NextBytes(data);
        const int level = 3;

        var framed = ParallelZstd.CompressFramed(data, length, level, chunkSize);

        Assert.Equal(CompressViaWriteStream(data, length, level, chunkSize), framed);

        using var reader = new ParallelZstd.ReadStream(new MemoryStream(framed));
        using var decompressed = new MemoryStream();
        reader.CopyTo(decompressed);
        Assert.Equal(data.AsSpan(0, length).ToArray(), decompressed.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void WriteStream_ManyChunksWithRecycledBuffers_MatchesCompressFramed(int maxDegreeOfParallelism)
    {
        // Far more chunks than the in-flight limit, with chunks that compress to very different
        // sizes, so recycled input/output buffers and compressors get reused with stale contents.
        const int chunkSize = 256;
        const int level = 3;
        var data = MixedCompressibility(chunkSize * 40 + 99);

        using var output = new MemoryStream();
        using (var writer = new ParallelZstd.WriteStream(output, level, maxDegreeOfParallelism, chunkSize))
        {
            // Odd-sized writes so chunk boundaries fall mid-write.
            for (var offset = 0; offset < data.Length; offset += 97)
            {
                writer.Write(data.AsSpan(offset, Math.Min(97, data.Length - offset)));
            }
        }

        Assert.Equal(ParallelZstd.CompressFramed(data, data.Length, level, chunkSize), output.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void ReadStream_ManyChunksWithRecycledBuffers_RoundTrips(int maxDegreeOfParallelism)
    {
        const int chunkSize = 256;
        var data = MixedCompressibility(chunkSize * 40 + 99);
        var framed = ParallelZstd.CompressFramed(data, data.Length, level: 3, chunkSize);

        using var reader = new ParallelZstd.ReadStream(new MemoryStream(framed), maxDegreeOfParallelism);
        var result = new byte[data.Length];
        var position = 0;

        // Alternate single-byte and odd-sized span reads so both paths cross chunk boundaries.
        while (position < result.Length)
        {
            if (position % 2 == 0)
            {
                var value = reader.ReadByte();
                Assert.NotEqual(-1, value);
                result[position++] = (byte)value;
            }
            else
            {
                var read = reader.Read(result.AsSpan(position, Math.Min(61, result.Length - position)));
                Assert.True(read > 0);
                position += read;
            }
        }

        Assert.Equal(data, result);
        Assert.Equal(-1, reader.ReadByte());
        Assert.Equal(0, reader.Read(new byte[1]));
    }

    [Fact]
    public void ReadStream_BinaryReaderPrimitivesAcrossChunkBoundaries_RoundTrip()
    {
        using var output = new MemoryStream();
        using (var writer = new ParallelZstd.WriteStream(output, level: 3, maxDegreeOfParallelism: 2, chunkSize: 64))
        using (var binaryWriter = new BinaryWriter(writer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            BinaryPrimitivesSample.Write(binaryWriter);
        }

        output.Position = 0;
        using var reader = new ParallelZstd.ReadStream(output, maxDegreeOfParallelism: 2);
        using var binaryReader = new BinaryReader(reader, System.Text.Encoding.UTF8, leaveOpen: true);

        BinaryPrimitivesSample.ReadAndAssert(binaryReader);
        Assert.Equal(-1, reader.ReadByte());
    }

    /// <summary>
    /// Alternating runs of random (incompressible) and zero (highly compressible) bytes, so
    /// consecutive chunks compress to very different sizes.
    /// </summary>
    private static byte[] MixedCompressibility(int length)
    {
        var data = new byte[length];
        var random = new Random(length);
        for (var offset = 0; offset < length; offset += 300)
        {
            if (offset / 300 % 2 == 0)
            {
                random.NextBytes(data.AsSpan(offset, Math.Min(300, length - offset)));
            }
        }

        return data;
    }

    private static byte[] CompressViaWriteStream(byte[] data, int length, int level, int chunkSize)
    {
        // Chunk size passed explicitly rather than via the global TestChunkSizeOverride, which
        // would race with other test classes running in parallel.
        using var output = new MemoryStream();
        using (var writer = new ParallelZstd.WriteStream(output, level, chunkSize: chunkSize))
        {
            writer.Write(data, 0, length);
        }

        return output.ToArray();
    }
}
