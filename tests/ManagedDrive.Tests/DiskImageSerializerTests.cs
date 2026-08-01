using System.IO.Compression;
using System.Security.Cryptography;

namespace ManagedDrive.Tests;

public sealed class DiskImageSerializerTests
{
    [Fact]
    public void Load_EncryptedImageWithoutPassword_ThrowsPasswordRequired()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "MyLabel", path, ImageCompressionLevel.Fastest,
                new ImageEncryptionInfo("s3cret", DiskImageSerializer.GenerateCek()));

            Assert.Throws<ImagePasswordRequiredException>(() =>
                DiskImageSerializer.Load(path, out _, out _, password: null, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_EncryptedImageWithWrongPassword_ThrowsPasswordIncorrect()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "MyLabel", path, ImageCompressionLevel.Fastest,
                new ImageEncryptionInfo("s3cret", DiskImageSerializer.GenerateCek()));

            Assert.Throws<ImagePasswordIncorrectException>(() =>
                DiskImageSerializer.Load(path, out _, out _, "wrong-password", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LegacyVersion1UncompressedImage_StillLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8))
            {
                writer.Write("MDRD"u8.ToArray());
                writer.Write(1); // legacy version, no compression flag byte
                writer.Write(2048UL);
                writer.Write("LegacyLabel");
                writer.Write(0); // no nodes
            }

            var loaded = DiskImageSerializer.Load(path, out var capacityBytes, out var volumeLabel, password: null, out var cek);

            Assert.Equal(2048UL, capacityBytes);
            Assert.Equal("LegacyLabel", volumeLabel);
            Assert.Equal(0, loaded.Count);
            Assert.Null(cek);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PeekHeader_EncryptedImage_ReturnsCapacityLabelWithoutPassword()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "MyLabel", path, ImageCompressionLevel.Fastest,
                new ImageEncryptionInfo("s3cret", DiskImageSerializer.GenerateCek()));

            DiskImageSerializer.PeekHeader(path, out var capacityBytes, out var volumeLabel, out var isEncrypted);

            Assert.Equal(1024UL * 1024, capacityBytes);
            Assert.Equal("MyLabel", volumeLabel);
            Assert.True(isEncrypted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Save_ConcurrentMapMutation_DoesNotThrowAndProducesLoadableImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            for (var i = 0; i < 50; i++)
            {
                map.Add($"\\File{i}.txt", MakeFile("hello world"u8.ToArray()));
            }

            var stop = new bool[1];
            var mutator = Task.Run(() =>
            {
                var i = 0;
                while (!Volatile.Read(ref stop[0]))
                {
                    var mutatePath = $"\\Mutating{i % 10}.txt";
                    map.Add(mutatePath, MakeFile("mutated"u8.ToArray()));
                    map.Remove(mutatePath);
                    i++;
                }
            }, TestContext.Current.CancellationToken);

            for (var i = 0; i < 20; i++)
            {
                DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "MyLabel", path, ImageCompressionLevel.Fastest);

                var loaded = DiskImageSerializer.Load(path, out _, out _, password: null, out _);
                Assert.True(loaded.Count >= 51);
            }

            Volatile.Write(ref stop[0], true);
            await mutator;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_EmptyNodeMap_ReportsOnlyOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();

            var reports = new List<double>();
            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "MyLabel", path, ImageCompressionLevel.Fastest,
                progress: new RecordingProgress(reports));

            Assert.Equal([1.0], reports);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_MultipleNodes_ReportsMonotonicProgressEndingAtOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            for (var i = 0; i < 5; i++)
            {
                map.Add($"\\File{i}.txt", MakeFile("hello world"u8.ToArray()));
            }

            var reports = new List<double>();
            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "MyLabel", path, ImageCompressionLevel.Fastest,
                progress: new RecordingProgress(reports));

            // Progress is now weighted by each node's allocation size rather than a flat
            // per-node fraction, so the leading directory node (zero bytes) doesn't move the
            // needle — only that overall monotonicity and the final 1.0 are guaranteed.
            Assert.NotEmpty(reports);
            Assert.True(reports[0] >= 0);
            Assert.Equal(1.0, reports[^1]);
            for (var i = 1; i < reports.Count; i++)
            {
                Assert.True(reports[i] >= reports[i - 1]);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ImageCompressionLevel.None)]
    [InlineData(ImageCompressionLevel.Fastest)]
    [InlineData(ImageCompressionLevel.Optimal)]
    [InlineData(ImageCompressionLevel.SmallestSize)]
    public void SaveThenLoad_RoundTrips_CapacityLabelAndNodes(ImageCompressionLevel level)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("hello world"u8.ToArray()));

            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "MyLabel", path, level);

            var loaded = DiskImageSerializer.Load(path, out var capacityBytes, out var volumeLabel, password: null, out var cek);

            Assert.Equal(1024UL * 1024, capacityBytes);
            Assert.Equal("MyLabel", volumeLabel);
            Assert.Equal(2, loaded.Count);
            Assert.True(loaded.TryGet("\\File.txt", out var node));
            Assert.Equal("hello world"u8.ToArray(), node!.FileData!.ToArray("hello world"u8.Length));
            Assert.Null(cek);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ImageCompressionLevel.None)]
    [InlineData(ImageCompressionLevel.Fastest)]
    [InlineData(ImageCompressionLevel.Optimal)]
    [InlineData(ImageCompressionLevel.SmallestSize)]
    public void SaveThenLoad_WithPassword_RoundTrips(ImageCompressionLevel level)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("hello world"u8.ToArray()));

            var cek = DiskImageSerializer.GenerateCek();
            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "MyLabel", path, level, new ImageEncryptionInfo("s3cret", cek));

            var loaded = DiskImageSerializer.Load(path, out var capacityBytes, out var volumeLabel, "s3cret", out var loadedCek);

            Assert.Equal(1024UL * 1024, capacityBytes);
            Assert.Equal("MyLabel", volumeLabel);
            Assert.Equal(2, loaded.Count);
            Assert.True(loaded.TryGet("\\File.txt", out var node));
            Assert.Equal("hello world"u8.ToArray(), node!.FileData!.ToArray("hello world"u8.Length));
            Assert.Equal(cek, loadedCek);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ImageCompressionLevel.None)]
    [InlineData(ImageCompressionLevel.Fastest)]
    public void SaveThenLoad_WithPasswordAcrossMultipleChunks_RoundTrips(ImageCompressionLevel level)
    {
        // Force a tiny chunk size so a handful of KB of node data spans several chunks,
        // exercising the version-4 chunked AES-GCM path without allocating a real 64 MB buffer.
        ChunkedGcm.TestChunkSizeOverride = 64;
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            for (var i = 0; i < 20; i++)
            {
                map.Add($"\\File{i}.txt", MakeFile(System.Text.Encoding.UTF8.GetBytes($"content for file number {i}")));
            }

            var cek = DiskImageSerializer.GenerateCek();
            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "ChunkedLabel", path, level, new ImageEncryptionInfo("s3cret", cek));

            var loaded = DiskImageSerializer.Load(path, out var capacityBytes, out var volumeLabel, "s3cret", out var loadedCek);

            Assert.Equal(1024UL * 1024, capacityBytes);
            Assert.Equal("ChunkedLabel", volumeLabel);
            Assert.Equal(21, loaded.Count);
            for (var i = 0; i < 20; i++)
            {
                Assert.True(loaded.TryGet($"\\File{i}.txt", out var node));
                var expected = System.Text.Encoding.UTF8.GetBytes($"content for file number {i}");
                Assert.Equal(expected, node!.FileData!.ToArray(expected.Length));
            }

            Assert.Equal(cek, loadedCek);
        }
        finally
        {
            ChunkedGcm.TestChunkSizeOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveThenLoad_WithWrongPasswordAcrossMultipleChunks_ThrowsPasswordIncorrect()
    {
        ChunkedGcm.TestChunkSizeOverride = 64;
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            for (var i = 0; i < 20; i++)
            {
                map.Add($"\\File{i}.txt", MakeFile("hello world"u8.ToArray()));
            }

            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "MyLabel", path, ImageCompressionLevel.Fastest,
                new ImageEncryptionInfo("s3cret", DiskImageSerializer.GenerateCek()));

            Assert.Throws<ImagePasswordIncorrectException>(() =>
                DiskImageSerializer.Load(path, out _, out _, "wrong-password", out _));
        }
        finally
        {
            ChunkedGcm.TestChunkSizeOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LegacyVersion3EncryptedWholeBlobImage_StillLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            const string password = "s3cret";
            const int iterations = 210_000;
            var cek = DiskImageSerializer.GenerateCek();

            byte[] nodeRegion;
            using (var nodeRegionStream = new MemoryStream())
            {
                using (var gzip = new GZipStream(nodeRegionStream, CompressionLevel.Fastest, leaveOpen: true))
                using (var payloadWriter = new BinaryWriter(gzip, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    payloadWriter.Write(0); // node count
                }

                nodeRegion = nodeRegionStream.ToArray();
            }

            var salt = RandomNumberGenerator.GetBytes(16);
            var kek = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
            var wrapNonce = RandomNumberGenerator.GetBytes(12);
            var wrappedCek = new byte[cek.Length];
            var wrapTag = new byte[16];
            using (var aesGcm = new AesGcm(kek, 16))
            {
                aesGcm.Encrypt(wrapNonce, cek, wrappedCek, wrapTag);
            }

            var dataNonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[nodeRegion.Length];
            var dataTag = new byte[16];
            using (var aesGcm = new AesGcm(cek, 16))
            {
                aesGcm.Encrypt(dataNonce, nodeRegion, ciphertext, dataTag);
            }

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8))
            {
                writer.Write("MDRD"u8.ToArray());
                writer.Write(3); // legacy whole-blob encrypted version
                writer.Write((byte)ImageCompressionLevel.Fastest);
                writer.Write((byte)1); // isEncrypted
                writer.Write(2048UL);
                writer.Write("LegacyEncryptedLabel");
                writer.Write(salt);
                writer.Write(iterations);
                writer.Write(wrapNonce);
                writer.Write(wrapTag);
                writer.Write(wrappedCek);
                writer.Write(dataNonce);
                writer.Write(dataTag);
                writer.Write(ciphertext);
            }

            var loaded = DiskImageSerializer.Load(path, out var capacityBytes, out var volumeLabel, password, out var loadedCek);

            Assert.Equal(2048UL, capacityBytes);
            Assert.Equal("LegacyEncryptedLabel", volumeLabel);
            Assert.Equal(0, loaded.Count);
            Assert.Equal(cek, loadedCek);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_WritesVersion5Header()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "MyLabel", path, ImageCompressionLevel.Fastest);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(stream);
            reader.ReadBytes(4); // magic
            Assert.Equal(5, reader.ReadInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ImageCompressionLevel.Fastest)]
    [InlineData(ImageCompressionLevel.Optimal)]
    [InlineData(ImageCompressionLevel.SmallestSize)]
    public void Save_Compressed_DoesNotUseGzip(ImageCompressionLevel level)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("hello world, this is compressible content"u8.ToArray()));
            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "MyLabel", path, level);

            var bytes = File.ReadAllBytes(path);

            // The gzip magic (0x1F 0x8B) should not appear right at the start of the node
            // region — a weak but simple signal that the payload was Zstd- rather than
            // gzip-compressed. (The node region starts right after the plaintext header.)
            var headerLength = 4 + 4 + 1 + 1 + 8 + (4 + "MyLabel".Length);
            Assert.False(bytes[headerLength] == 0x1F && bytes[headerLength + 1] == 0x8B);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LegacyVersion4GzipCompressedImage_StillLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            byte[] nodeRegion;
            using (var nodeRegionStream = new MemoryStream())
            {
                using (var gzip = new GZipStream(nodeRegionStream, CompressionLevel.Fastest, leaveOpen: true))
                using (var payloadWriter = new BinaryWriter(gzip, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    payloadWriter.Write(0); // node count
                }

                nodeRegion = nodeRegionStream.ToArray();
            }

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8))
            {
                writer.Write("MDRD"u8.ToArray());
                writer.Write(4); // legacy gzip-compressed version, unencrypted
                writer.Write((byte)ImageCompressionLevel.Fastest);
                writer.Write((byte)0); // isEncrypted
                writer.Write(2048UL);
                writer.Write("LegacyGzipLabel");
                writer.Write(nodeRegion);
            }

            var loaded = DiskImageSerializer.Load(path, out var capacityBytes, out var volumeLabel, password: null, out var cek);

            Assert.Equal(2048UL, capacityBytes);
            Assert.Equal("LegacyGzipLabel", volumeLabel);
            Assert.Equal(0, loaded.Count);
            Assert.Null(cek);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveThenLoad_WithParallelZstdAcrossMultipleChunks_RoundTrips()
    {
        // Force a tiny chunk size so a handful of KB of node data spans several independently
        // compressed chunks, exercising the parallel Zstd compression path without allocating a
        // real multi-megabyte buffer.
        ParallelZstd.TestChunkSizeOverride = 64;
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            for (var i = 0; i < 50; i++)
            {
                map.Add($"\\File{i}.txt", MakeFile(System.Text.Encoding.UTF8.GetBytes($"content for file number {i}")));
            }

            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "ChunkedLabel", path, ImageCompressionLevel.Fastest);

            var loaded = DiskImageSerializer.Load(path, out var capacityBytes, out var volumeLabel, password: null, out var cek);

            Assert.Equal(1024UL * 1024, capacityBytes);
            Assert.Equal("ChunkedLabel", volumeLabel);
            Assert.Equal(51, loaded.Count);
            for (var i = 0; i < 50; i++)
            {
                Assert.True(loaded.TryGet($"\\File{i}.txt", out var node));
                var expected = System.Text.Encoding.UTF8.GetBytes($"content for file number {i}");
                Assert.Equal(expected, node!.FileData!.ToArray(expected.Length));
            }

            Assert.Null(cek);
        }
        finally
        {
            ParallelZstd.TestChunkSizeOverride = null;
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(19)]
    [InlineData(22)]
    public void SaveThenLoad_WithCustomZstdLevel_RoundTrips(int customZstdLevel)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("hello world"u8.ToArray()));

            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "MyLabel", path, ImageCompressionLevel.Fastest,
                customZstdLevel: customZstdLevel);

            var loaded = DiskImageSerializer.Load(path, out var capacityBytes, out var volumeLabel, password: null, out var cek);

            Assert.Equal(1024UL * 1024, capacityBytes);
            Assert.Equal("MyLabel", volumeLabel);
            Assert.Equal(2, loaded.Count);
            Assert.True(loaded.TryGet("\\File.txt", out var node));
            Assert.Equal("hello world"u8.ToArray(), node!.FileData!.ToArray("hello world"u8.Length));
            Assert.Null(cek);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static FileNode MakeDir() => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
    };

    private static FileNode MakeFile(byte[] content)
    {
        var aligned = FileNode.AlignToAllocationUnit((ulong)content.Length);

        return new()
        {
            FileInfo =
            {
                FileAttributes = (uint)FileAttributes.Normal,
                FileSize = (ulong)content.Length,
                AllocationSize = aligned,
            },
            FileData = FileContent.FromSpan(content, aligned),
        };
    }
}