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
            DiskImageSerializer.SaveIncremental(map, 4UL * 1024 * 1024, "Label", path, 1);
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
    [InlineData(4, 4)]
    public void TrySetCapacityInPlace_ImageWithOtherVersion_ReturnsFalseAndLeavesFileUntouched(
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

    /// <summary>
    /// A continuous image (version 5, or version 7 continuous for links, either one encrypted or
    /// not) is raised in place: only the capacity field changes and the image still loads.
    /// </summary>
    /// <param name="encrypted">Whether the image is encrypted.</param>
    /// <param name="withLink">Whether the disk holds a link, which writes version 7 continuous.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TrySetCapacityInPlace_ContinuousImageRaised_ChangesOnlyTheCapacityField(bool encrypted, bool withLink)
    {
        var path = NewImagePath();
        try
        {
            var password = encrypted ? "s3cret" : null;
            SaveContinuous(path, 4UL * 1024 * 1024, password, withLink);
            var before = File.ReadAllBytes(path);
            var capacityOffset = withLink ? CapacityOffset : CapacityOffset - 1;

            var raised = DiskImageSerializer.TrySetCapacityInPlace(path, 8UL * 1024 * 1024);

            var after = File.ReadAllBytes(path);
            Assert.True(raised);
            Assert.Equal(before.Length, after.Length);
            Assert.Equal(before[..(int)capacityOffset], after[..(int)capacityOffset]);
            Assert.Equal(
                before[(int)(capacityOffset + CapacityLength)..],
                after[(int)(capacityOffset + CapacityLength)..]);

            var loaded = DiskImageSerializer.Load(path, out var capacity, out _, password, out _);
            Assert.Equal(8UL * 1024 * 1024, capacity);
            Assert.True(loaded.TryGet("\\File.txt", out _));
            Assert.Equal(withLink, loaded.TryGet("\\Link", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A continuous image is never lowered in place, even when the caller vouches for the node
    /// count: without a segment index that cannot be checked.
    /// </summary>
    /// <param name="withLink">Whether the disk holds a link, which writes version 7 continuous.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrySetCapacityInPlace_ContinuousImageLowered_ReturnsFalseAndLeavesFileUntouched(bool withLink)
    {
        var path = NewImagePath();
        try
        {
            SaveContinuous(path, 4UL * 1024 * 1024, password: null, withLink);
            var before = File.ReadAllBytes(path);

            var lowered = DiskImageSerializer.TrySetCapacityInPlace(path, 2UL * 1024 * 1024, nodeCountForDecrease: withLink ? 3 : 2);

            Assert.False(lowered);
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

    /// <summary>
    /// A full save records the larger of the capacity it was given and the one read once every
    /// node is written, in every layout (version 5, encrypted, version 7 continuous for links), and
    /// the image still loads with its contents.
    /// </summary>
    /// <param name="encrypted">Whether the image is encrypted.</param>
    /// <param name="withLink">Whether the disk holds a link, which writes version 7 continuous.</param>
    /// <param name="finalCapacity">The capacity read at the end, or <c>0</c> for no provider.</param>
    /// <param name="expected">The capacity the loaded header must hold.</param>
    [Theory]
    [InlineData(false, false, 8UL << 20, 8UL << 20)]
    [InlineData(true, false, 8UL << 20, 8UL << 20)]
    [InlineData(false, true, 8UL << 20, 8UL << 20)]
    [InlineData(true, true, 8UL << 20, 8UL << 20)]
    [InlineData(false, false, 2UL << 20, 4UL << 20)]
    [InlineData(false, false, 0UL, 4UL << 20)]
    public void Save_FinalCapacityProvider_HeaderHoldsLargerCapacity(bool encrypted, bool withLink, ulong finalCapacity, ulong expected)
    {
        var path = NewImagePath();
        try
        {
            var map = NewMap(withLink);
            var password = encrypted ? "s3cret" : null;
            ImageEncryptionInfo? encryption = password is null ? null : new(password, DiskImageSerializer.GenerateCek());
            Func<ulong>? provider = finalCapacity == 0 ? null : () => finalCapacity;

            DiskImageSerializer.Save(map, 4UL << 20, "Label", path, 1, encryption, finalCapacityBytes: provider);

            var loaded = DiskImageSerializer.Load(path, out var capacity, out var label, password, out _);
            Assert.Equal(expected, capacity);
            Assert.Equal("Label", label);
            Assert.True(loaded.TryGet("\\File.txt", out var node));
            Assert.Equal("hello world"u8.ToArray(), node!.FileData!.ToArray((long)node.FileInfo.FileSize));
            Assert.Equal(withLink, loaded.TryGet("\\Link", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string NewImagePath() => Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");

    private static FileNodeMap NewMap(bool withLink = false)
    {
        var map = new FileNodeMap();
        map.Add("\\", new FileNode { FileInfo = { FileAttributes = (uint)FileAttributes.Directory } });
        map.Add("\\File.txt", NewFileNode("hello world"u8.ToArray()));
        if (withLink)
        {
            // A symbolic-link reparse buffer: tag, data length 4, reserved, data.
            var link = NewFileNode([]);
            link.ApplyReparseData([0x0C, 0x00, 0x00, 0xA0, 0x04, 0x00, 0x00, 0x00, 1, 2, 3, 4]);
            map.Add("\\Link", link);
        }

        return map;
    }

    /// <summary>
    /// Writes a continuous image: version 5, or version 7 continuous when the disk holds a link.
    /// </summary>
    /// <param name="path">The image path.</param>
    /// <param name="capacity">The capacity to record.</param>
    /// <param name="password">The password, or <c>null</c> for an unencrypted image.</param>
    /// <param name="withLink">Whether the disk holds a link.</param>
    private static void SaveContinuous(string path, ulong capacity, string? password, bool withLink)
    {
        ImageEncryptionInfo? encryption = password is null
            ? null
            : new ImageEncryptionInfo(password, DiskImageSerializer.GenerateCek());

        DiskImageSerializer.Save(NewMap(withLink), capacity, "Label", path, 1, encryption);
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
            NewMap(), capacity, "Label", path, 1, encryption);
    }
}
