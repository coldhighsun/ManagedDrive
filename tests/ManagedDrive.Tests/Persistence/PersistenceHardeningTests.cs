using System.Buffers.Binary;
using System.Text;

namespace ManagedDrive.Tests;

/// <summary>
/// Malformed and damaged image input must fail the load with a clear error instead of loading
/// silently or allocating memory from an unvalidated size field.
/// </summary>
public sealed class PersistenceHardeningTests
{
    /// <summary>
    /// A flipped content byte in an unencrypted segment fails the load through the segment digest.
    /// </summary>
    [Fact]
    public void Load_UnencryptedSegmentWithFlippedContentByte_ThrowsInvalidDataException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("distinctive-content-for-bit-rot"u8.ToArray()));
            DiskImageSerializer.SaveSegmentedForTest(map, 4 * 1024 * 1024, "Label", path,
                ImageCompressionLevel.None, encryption: null, segmentTargetBytes: 1 << 20);

            var bytes = File.ReadAllBytes(path);
            var position = bytes.AsSpan().IndexOf("distinctive-content-for-bit-rot"u8);
            Assert.True(position >= 0);
            bytes[position + 3] ^= 0x01;
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(
                () => DiskImageSerializer.Load(path, out _, out _, password: null, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// An undamaged segmented image still loads with the digest check in place.
    /// </summary>
    [Fact]
    public void Load_UntouchedSegmentedImage_StillLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("intact"u8.ToArray()));
            DiskImageSerializer.SaveSegmentedForTest(map, 4 * 1024 * 1024, "Label", path,
                ImageCompressionLevel.None, encryption: null, segmentTargetBytes: 1 << 20);

            var loaded = DiskImageSerializer.Load(path, out _, out _, password: null, out _);

            Assert.Equal(2, loaded.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A node claiming far more space than the disk capacity fails the load before its content is allocated.
    /// </summary>
    [Fact]
    public void Load_NodeLargerThanDiskCapacity_ThrowsInvalidDataException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            // A sparse preallocation: 2 GiB of allocation costs only a small chunk table, but it is
            // far beyond this 1 MiB disk's capacity (plus the loader's slack).
            var huge = new FileNode
            {
                FileInfo =
                {
                    FileAttributes = (uint)FileAttributes.Normal,
                    FileSize = 0,
                    AllocationSize = 2UL * 1024 * 1024 * 1024,
                },
                FileData = FileContent.CreateZeroed(2UL * 1024 * 1024 * 1024),
            };
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\Huge.bin", huge);
            DiskImageSerializer.SaveSegmentedForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.None, encryption: null, segmentTargetBytes: 1 << 20);

            Assert.Throws<InvalidDataException>(
                () => DiskImageSerializer.Load(path, out _, out _, password: null, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A negative node count fails the load instead of loading as an empty disk.
    /// </summary>
    [Fact]
    public void Load_NegativeNodeCount_ThrowsInvalidDataException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            DiskImageSerializer.Save(map, 1024 * 1024, "L", path, ImageCompressionLevel.None);

            // Layout: magic(4) version(4) level(1) isEncrypted(1) capacity(8) label(1+1), then the
            // node count of the uncompressed node region.
            var bytes = File.ReadAllBytes(path);
            const int countOffset = 4 + 4 + 1 + 1 + 8 + 2;
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(countOffset)));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(countOffset), -1);
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(
                () => DiskImageSerializer.Load(path, out _, out _, password: null, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A header cut short inside the key material is reported as damage, not as a wrong password.
    /// </summary>
    [Fact]
    public void Load_TruncatedEncryptedHeader_DoesNotReportWrongPassword()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            DiskImageSerializer.Save(map, 1024 * 1024, "L", path, ImageCompressionLevel.None,
                new ImageEncryptionInfo("pw", DiskImageSerializer.GenerateCek()));

            // Cut inside the wrapped key material that follows the plaintext header fields.
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes.AsSpan(0, 4 + 4 + 1 + 1 + 8 + 2 + 20).ToArray());

            var exception = Record.Exception(
                () => DiskImageSerializer.Load(path, out _, out _, "pw", out _));

            Assert.NotNull(exception);
            Assert.IsNotType<ImagePasswordIncorrectException>(exception);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A Zstd chunk length far beyond the chunk size is rejected before any buffer is allocated.
    /// </summary>
    [Fact]
    public void ZstdReadStream_ChunkLengthBeyondBound_ThrowsInvalidDataException()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        using var stream = new ParallelZstd.ReadStream(new MemoryStream(header));

        Assert.Throws<InvalidDataException>(() => stream.ReadByte());
    }

    /// <summary>
    /// An encrypted chunk length far beyond the chunk size is rejected before any buffer is allocated.
    /// </summary>
    [Fact]
    public void GcmReadStream_ChunkLengthBeyondBound_ThrowsInvalidDataException()
    {
        var header = new byte[4 + ChunkedGcm.TagSize];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        using var stream = new ChunkedGcm.ReadStream(
            new MemoryStream(header), DiskImageSerializer.GenerateCek(), new byte[ChunkedGcm.NonceSize]);

        Assert.Throws<InvalidDataException>(() => stream.ReadByte());
    }

    /// <summary>
    /// A security-descriptor length far beyond any real descriptor is rejected before it is allocated.
    /// </summary>
    [Fact]
    public void ReadMetadata_SecurityDescriptorLengthBeyondBound_ThrowsInvalidDataException()
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            NodeMetadataIO.WriteMetadata(writer, "\\File.txt", new Fsp.Interop.FileInfo(), null);
        }

        // The record ends with the 4-byte descriptor length (zero: none was written).
        var bytes = buffer.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), int.MaxValue);
        using var reader = new BinaryReader(new MemoryStream(bytes));

        Assert.Throws<InvalidDataException>(() => NodeMetadataIO.ReadMetadata(reader));
    }

    /// <summary>
    /// Creates a directory node.
    /// </summary>
    private static FileNode MakeDir() => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
    };

    /// <summary>
    /// Creates a file node holding <paramref name="content"/>.
    /// </summary>
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
