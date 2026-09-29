using ZstdSharp;

namespace ManagedDrive.Tests;

/// <summary>
/// Guards the assumption the parallel Zstd node-region writer relies on: independently
/// compressed Zstd frames, written back-to-back into one stream, decompress transparently as
/// if they were a single frame. If this ever stopped being true, <c>DecompressionStream</c>
/// would need explicit per-chunk framing instead.
/// </summary>
public sealed class ZstdConcatenatedFramesTests
{
    [Fact]
    public void DecompressionStream_ReadsMultipleConcatenatedIndependentFrames()
    {
        var chunk1 = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Hello World! ", 500)));
        var chunk2 = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Goodbye World! ", 500)));

        byte[] frame1, frame2;
        using (var c1 = new Compressor(3))
        {
            frame1 = c1.Wrap(chunk1).ToArray();
        }

        using (var c2 = new Compressor(3))
        {
            frame2 = c2.Wrap(chunk2).ToArray();
        }

        using var concatenated = new MemoryStream();
        concatenated.Write(frame1);
        concatenated.Write(frame2);
        concatenated.Position = 0;

        using var decompressed = new MemoryStream();
        using (var ds = new DecompressionStream(concatenated))
        {
            ds.CopyTo(decompressed);
        }

        var expected = new byte[chunk1.Length + chunk2.Length];
        Buffer.BlockCopy(chunk1, 0, expected, 0, chunk1.Length);
        Buffer.BlockCopy(chunk2, 0, expected, chunk1.Length, chunk2.Length);

        Assert.Equal(expected, decompressed.ToArray());
    }
}
