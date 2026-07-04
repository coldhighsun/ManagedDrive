using ManagedDrive.Core;

namespace ManagedDrive.Tests;

public sealed class DiskImageSerializerTests
{
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

            var loaded = DiskImageSerializer.Load(path, out var capacityBytes, out var volumeLabel);

            Assert.Equal(2048UL, capacityBytes);
            Assert.Equal("LegacyLabel", volumeLabel);
            Assert.Equal(0, loaded.Count);
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

            var loaded = DiskImageSerializer.Load(path, out var capacityBytes, out var volumeLabel);

            Assert.Equal(1024UL * 1024, capacityBytes);
            Assert.Equal("MyLabel", volumeLabel);
            Assert.Equal(2, loaded.Count);
            Assert.True(loaded.TryGet("\\File.txt", out var node));
            Assert.Equal("hello world"u8.ToArray(), node!.FileData!.Take("hello world"u8.Length).ToArray());
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