using ManagedDrive.Core;

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
            });

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
            Assert.Equal("hello world"u8.ToArray(), node!.FileData!.Take("hello world"u8.Length).ToArray());
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
            Assert.Equal("hello world"u8.ToArray(), node!.FileData!.Take("hello world"u8.Length).ToArray());
            Assert.Equal(cek, loadedCek);
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
        var data = new byte[aligned];
        Buffer.BlockCopy(content, 0, data, 0, content.Length);

        return new()
        {
            FileInfo =
            {
                FileAttributes = (uint)FileAttributes.Normal,
                FileSize = (ulong)content.Length,
                AllocationSize = aligned,
            },
            FileData = data,
        };
    }
}