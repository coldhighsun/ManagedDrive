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

    private static byte[] CompressViaWriteStream(byte[] data, int length, int level, int chunkSize)
    {
        // Mirrors WriteStream's per-chunk compression and framing. WriteStream itself only takes
        // its chunk size from the global TestChunkSizeOverride, which would race with other test
        // classes running in parallel.
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        for (var offset = 0; offset < length; offset += chunkSize)
        {
            using var compressor = new ZstdSharp.Compressor(level);
            var chunk = compressor.Wrap(data.AsSpan(offset, Math.Min(chunkSize, length - offset))).ToArray();
            writer.Write(chunk.Length);
            writer.Write(chunk);
        }

        writer.Write(0);
        writer.Flush();
        return output.ToArray();
    }
}
