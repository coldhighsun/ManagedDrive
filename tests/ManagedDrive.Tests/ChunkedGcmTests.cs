using System.Security.Cryptography;

namespace ManagedDrive.Tests;

public sealed class ChunkedGcmTests : IDisposable
{
    public ChunkedGcmTests()
    {
        // Force a tiny chunk size so multi-chunk paths are exercised without huge buffers.
        ChunkedGcm.TestChunkSizeOverride = 16;
    }

    public void Dispose()
    {
        ChunkedGcm.TestChunkSizeOverride = null;
    }

    [Fact]
    public void WriteThenRead_EmptyPlaintext_RoundTrips()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(ChunkedGcm.NonceSize);
        using var buffer = new MemoryStream();

        using (var write = new ChunkedGcm.WriteStream(buffer, key, nonce, ChunkedGcm.ChunkSize))
        {
            write.Complete();
        }

        buffer.Position = 0;
        using var read = new ChunkedGcm.ReadStream(buffer, key, nonce);
        var result = new byte[1];
        Assert.Equal(0, read.Read(result, 0, 1));
    }

    [Fact]
    public void WriteThenRead_SingleByte_RoundTrips()
    {
        var plaintext = new byte[] { 0x42 };

        var roundTripped = RoundTrip(plaintext);

        Assert.Equal(plaintext, roundTripped);
    }

    [Fact]
    public void WriteThenRead_ExactlyOneChunk_RoundTrips()
    {
        var plaintext = RandomNumberGenerator.GetBytes(ChunkedGcm.ChunkSize);

        var roundTripped = RoundTrip(plaintext);

        Assert.Equal(plaintext, roundTripped);
    }

    [Fact]
    public void WriteThenRead_SpansMultipleChunks_RoundTrips()
    {
        var plaintext = RandomNumberGenerator.GetBytes((ChunkedGcm.ChunkSize * 3) + 5);

        var roundTripped = RoundTrip(plaintext);

        Assert.Equal(plaintext, roundTripped);
    }

    [Fact]
    public void Read_TamperedCiphertext_ThrowsCryptographicException()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(ChunkedGcm.NonceSize);
        var plaintext = RandomNumberGenerator.GetBytes(40);
        using var buffer = new MemoryStream();

        using (var write = new ChunkedGcm.WriteStream(buffer, key, nonce, ChunkedGcm.ChunkSize))
        {
            write.Write(plaintext, 0, plaintext.Length);
            write.Complete();
        }

        var bytes = buffer.ToArray();
        // Flip a bit inside the first chunk's ciphertext (past the 4-byte length + 16-byte tag header).
        bytes[4 + ChunkedGcm.TagSize] ^= 0xFF;
        using var tampered = new MemoryStream(bytes);

        using var read = new ChunkedGcm.ReadStream(tampered, key, nonce);
        var result = new byte[plaintext.Length];
        Assert.Throws<AuthenticationTagMismatchException>(() => read.Read(result, 0, result.Length));
    }

    [Fact]
    public void Read_WrongKey_ThrowsCryptographicException()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(ChunkedGcm.NonceSize);
        var plaintext = RandomNumberGenerator.GetBytes(40);
        using var buffer = new MemoryStream();

        using (var write = new ChunkedGcm.WriteStream(buffer, key, nonce, ChunkedGcm.ChunkSize))
        {
            write.Write(plaintext, 0, plaintext.Length);
            write.Complete();
        }

        buffer.Position = 0;
        using var read = new ChunkedGcm.ReadStream(buffer, wrongKey, nonce);
        var result = new byte[plaintext.Length];
        Assert.Throws<AuthenticationTagMismatchException>(() => read.Read(result, 0, result.Length));
    }

    [Fact]
    public void DeriveChunkNonce_DifferentChunkIndices_ProduceDifferentNonces()
    {
        var baseNonce = RandomNumberGenerator.GetBytes(ChunkedGcm.NonceSize);

        var nonce0 = ChunkedGcm.DeriveChunkNonce(baseNonce, 0);
        var nonce1 = ChunkedGcm.DeriveChunkNonce(baseNonce, 1);

        Assert.NotEqual(nonce0, nonce1);
    }

    [Fact]
    public void DeriveChunkNonce_SameChunkIndex_IsDeterministic()
    {
        var baseNonce = RandomNumberGenerator.GetBytes(ChunkedGcm.NonceSize);

        var first = ChunkedGcm.DeriveChunkNonce(baseNonce, 7);
        var second = ChunkedGcm.DeriveChunkNonce(baseNonce, 7);

        Assert.Equal(first, second);
    }

    private static byte[] RoundTrip(byte[] plaintext)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(ChunkedGcm.NonceSize);
        using var buffer = new MemoryStream();

        using (var write = new ChunkedGcm.WriteStream(buffer, key, nonce, ChunkedGcm.ChunkSize))
        {
            write.Write(plaintext, 0, plaintext.Length);
            write.Complete();
        }

        buffer.Position = 0;
        using var read = new ChunkedGcm.ReadStream(buffer, key, nonce);
        using var output = new MemoryStream();
        read.CopyTo(output);
        return output.ToArray();
    }
}
