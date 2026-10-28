namespace ManagedDrive.Tests;

public sealed class DiskImageCapacityPatchTests
{
    private const long CapacityOffset = 11;
    private const int CapacityLength = sizeof(ulong);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrySetCapacityInPlace_SegmentedImage_ChangesOnlyTheCapacityField(bool encrypted)
    {
        var path = NewImagePath();
        try
        {
            var password = encrypted ? "s3cret" : null;
            SaveSegmented(path, 4UL * 1024 * 1024, password);
            var before = File.ReadAllBytes(path);

            var raised = DiskImageSerializer.TrySetCapacityInPlace(path, 8UL * 1024 * 1024);

            var after = File.ReadAllBytes(path);
            Assert.True(raised);
            Assert.Equal(before.Length, after.Length);
            Assert.Equal(before[..(int)CapacityOffset], after[..(int)CapacityOffset]);
            Assert.Equal(
                before[(int)(CapacityOffset + CapacityLength)..],
                after[(int)(CapacityOffset + CapacityLength)..]);

            var loaded = DiskImageSerializer.Load(path, out var capacity, out var label, password, out _);
            Assert.Equal(8UL * 1024 * 1024, capacity);
            Assert.Equal("Label", label);
            Assert.True(loaded.TryGet("\\File.txt", out var node));
            Assert.Equal("hello world"u8.ToArray(), node!.FileData!.ToArray((long)node.FileInfo.FileSize));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TrySetCapacityInPlace_SameCapacity_ReturnsTrueAndLeavesFileUntouched()
    {
        var path = NewImagePath();
        try
        {
            SaveSegmented(path, 4UL * 1024 * 1024, password: null);
            var before = File.ReadAllBytes(path);

            var raised = DiskImageSerializer.TrySetCapacityInPlace(path, 4UL * 1024 * 1024);

            Assert.True(raised);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TrySetCapacityInPlace_SmallerCapacity_ReturnsFalseAndLeavesFileUntouched()
    {
        var path = NewImagePath();
        try
        {
            SaveSegmented(path, 4UL * 1024 * 1024, password: null);
            var before = File.ReadAllBytes(path);

            var raised = DiskImageSerializer.TrySetCapacityInPlace(path, 2UL * 1024 * 1024);

            Assert.False(raised);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrySetCapacityInPlace_SmallerCapacityWithMatchingNodeCount_WritesItAndImageStillLoads(bool encrypted)
    {
        var path = NewImagePath();
        try
        {
            var password = encrypted ? "s3cret" : null;
            SaveSegmented(path, 4UL * 1024 * 1024, password);

            var changed = DiskImageSerializer.TrySetCapacityInPlace(
                path, 2UL * 1024 * 1024, nodeCountForDecrease: NewMap().Count);

            Assert.True(changed);
            var loaded = DiskImageSerializer.Load(path, out var capacity, out _, password, out _);
            Assert.Equal(2UL * 1024 * 1024, capacity);
            Assert.True(loaded.TryGet("\\File.txt", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void TrySetCapacityInPlace_SmallerCapacityWithDifferentNodeCount_ReturnsFalseAndLeavesFileUntouched(int offset)
    {
        var path = NewImagePath();
        try
        {
            SaveSegmented(path, 4UL * 1024 * 1024, password: null);
            var before = File.ReadAllBytes(path);

            var changed = DiskImageSerializer.TrySetCapacityInPlace(
                path, 2UL * 1024 * 1024, nodeCountForDecrease: NewMap().Count + offset);

            Assert.False(changed);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TrySetCapacityInPlace_ImageHoldingNodesTheLoadDropped_RefusesToShrink()
    {
        var path = NewImagePath();
        try
        {
            var map = NewMap();
            map.Add("\\orphan:s", NewFileNode("stream"u8.ToArray()));
            DiskImageSerializer.SaveIncremental(map, 4UL * 1024 * 1024, "Label", path, ImageCompressionLevel.Fastest);
            var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);
            Assert.False(loaded.TryGet("\\orphan:s", out _));
            var before = File.ReadAllBytes(path);

            var changed = DiskImageSerializer.TrySetCapacityInPlace(
                path, 2UL * 1024 * 1024, nodeCountForDecrease: loaded.Count);

            Assert.False(changed);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(4, 6)]
    [InlineData(10, 0)]
    public void TrySetCapacityInPlace_ImageWithOtherVersionOrLayout_ReturnsFalseAndLeavesFileUntouched(
        int offset, byte value)
    {
        var path = NewImagePath();
        try
        {
            SaveSegmented(path, 4UL * 1024 * 1024, password: null);
            var bytes = File.ReadAllBytes(path);
            bytes[offset] = value;
            File.WriteAllBytes(path, bytes);

            var changed = DiskImageSerializer.TrySetCapacityInPlace(path, 8UL * 1024 * 1024);

            Assert.False(changed);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TrySetCapacityInPlace_MissingFile_ReturnsFalse()
    {
        var path = NewImagePath();

        var raised = DiskImageSerializer.TrySetCapacityInPlace(path, 8UL * 1024 * 1024);

        Assert.False(raised);
    }

    [Fact]
    public void TrySetCapacityInPlace_NonSegmentedImage_ReturnsFalseAndLeavesFileUntouched()
    {
        var path = NewImagePath();
        try
        {
            var map = NewMap();
            DiskImageSerializer.Save(map, 4UL * 1024 * 1024, "Label", path, ImageCompressionLevel.Fastest);
            var before = File.ReadAllBytes(path);

            var raised = DiskImageSerializer.TrySetCapacityInPlace(path, 8UL * 1024 * 1024);

            Assert.False(raised);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TrySetCapacityInPlace_NotAnImage_ReturnsFalse()
    {
        var path = NewImagePath();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);

            var raised = DiskImageSerializer.TrySetCapacityInPlace(path, 8UL * 1024 * 1024);

            Assert.False(raised);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string NewImagePath() => Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");

    private static FileNodeMap NewMap()
    {
        var map = new FileNodeMap();
        map.Add("\\", new FileNode { FileInfo = { FileAttributes = (uint)FileAttributes.Directory } });
        map.Add("\\File.txt", NewFileNode("hello world"u8.ToArray()));
        return map;
    }

    private static FileNode NewFileNode(byte[] content)
    {
        var aligned = FileNode.AlignToAllocationUnit((ulong)content.Length);

        return new FileNode
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

    private static void SaveSegmented(string path, ulong capacity, string? password)
    {
        ImageEncryptionInfo? encryption = password is null
            ? null
            : new ImageEncryptionInfo(password, DiskImageSerializer.GenerateCek());

        DiskImageSerializer.SaveIncremental(
            NewMap(), capacity, "Label", path, ImageCompressionLevel.Fastest, encryption);
    }
}
