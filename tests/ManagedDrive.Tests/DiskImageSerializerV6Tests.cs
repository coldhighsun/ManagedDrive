namespace ManagedDrive.Tests;

public sealed class DiskImageSerializerV6Tests
{
    [Theory]
    [InlineData(ImageCompressionLevel.None, false)]
    [InlineData(ImageCompressionLevel.None, true)]
    [InlineData(ImageCompressionLevel.Fastest, false)]
    [InlineData(ImageCompressionLevel.Fastest, true)]
    public void Load_SegmentedImage_RoundTripsNodeContentAndMetadata(ImageCompressionLevel level, bool encrypted)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            for (var i = 0; i < 10; i++)
            {
                map.Add($"\\File{i}.txt", MakeFile(System.Text.Encoding.UTF8.GetBytes($"content for file number {i}")));
            }

            ImageEncryptionInfo? encryption = encrypted
                ? new ImageEncryptionInfo("s3cret", DiskImageSerializer.GenerateCek())
                : null;

            // Small segment size (in bytes) relative to total node content so the round trip
            // actually exercises multiple segments, not just one.
            DiskImageSerializer.SaveSegmentedForTest(map, capacityBytes: 4 * 1024 * 1024, "SegLabel", path, level,
                encryption, segmentTargetBytes: 1500);

            var loaded = DiskImageSerializer.Load(path, out var capacityBytes, out var volumeLabel,
                encrypted ? "s3cret" : null, out var cek);

            Assert.Equal(4UL * 1024 * 1024, capacityBytes);
            Assert.Equal("SegLabel", volumeLabel);
            Assert.Equal(11, loaded.Count);
            Assert.Equal(encrypted, cek is not null);

            for (var i = 0; i < 10; i++)
            {
                Assert.True(loaded.TryGet($"\\File{i}.txt", out var node));
                var expected = System.Text.Encoding.UTF8.GetBytes($"content for file number {i}");
                Assert.Equal(expected, node!.FileData!.ToArray((long)node.FileInfo.FileSize));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_SegmentedEncryptedImageWithWrongPassword_ThrowsPasswordIncorrect()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("hello world"u8.ToArray()));

            DiskImageSerializer.SaveSegmentedForTest(map, capacityBytes: 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest, new ImageEncryptionInfo("s3cret", DiskImageSerializer.GenerateCek()),
                segmentTargetBytes: 4096);

            Assert.Throws<ImagePasswordIncorrectException>(() =>
                DiskImageSerializer.Load(path, out _, out _, "wrong-password", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_SegmentedEncryptedImageWithoutPassword_ThrowsPasswordRequired()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("hello world"u8.ToArray()));

            DiskImageSerializer.SaveSegmentedForTest(map, capacityBytes: 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest, new ImageEncryptionInfo("s3cret", DiskImageSerializer.GenerateCek()),
                segmentTargetBytes: 4096);

            Assert.Throws<ImagePasswordRequiredException>(() =>
                DiskImageSerializer.Load(path, out _, out _, password: null, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PeekHeader_SegmentedImage_ReturnsCapacityLabelWithoutPassword()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("hello world"u8.ToArray()));

            DiskImageSerializer.SaveSegmentedForTest(map, capacityBytes: 2 * 1024 * 1024, "PeekLabel", path,
                ImageCompressionLevel.Fastest, new ImageEncryptionInfo("s3cret", DiskImageSerializer.GenerateCek()),
                segmentTargetBytes: 4096);

            DiskImageSerializer.PeekHeader(path, out var capacityBytes, out var volumeLabel, out var isEncrypted);

            Assert.Equal(2UL * 1024 * 1024, capacityBytes);
            Assert.Equal("PeekLabel", volumeLabel);
            Assert.True(isEncrypted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_SegmentedImage_PreservesSecurityDescriptorAndAttributes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            var node = MakeFile("hello world"u8.ToArray());
            node.FileSecurity = [1, 2, 3, 4];
            map.Add("\\File.txt", node);

            DiskImageSerializer.SaveSegmentedForTest(map, capacityBytes: 1024 * 1024, "Label", path,
                ImageCompressionLevel.None, encryption: null, segmentTargetBytes: 4096);

            var loaded = DiskImageSerializer.Load(path, out _, out _, password: null, out _);

            Assert.True(loaded.TryGet("\\File.txt", out var loadedNode));
            Assert.Equal(node.FileSecurity, loadedNode!.FileSecurity);
            Assert.Equal(node.FileInfo.FileAttributes, loadedNode.FileInfo.FileAttributes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_SegmentedImage_EmptyNodeMap_LoadsZeroNodes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();

            DiskImageSerializer.SaveSegmentedForTest(map, capacityBytes: 1024, "Empty", path,
                ImageCompressionLevel.None, encryption: null, segmentTargetBytes: 4096);

            var loaded = DiskImageSerializer.Load(path, out _, out _, password: null, out _);

            Assert.Equal(0, loaded.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveIncremental_ProducesLoadableV6Image()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("hello world"u8.ToArray()));

            DiskImageSerializer.SaveIncremental(map, capacityBytes: 1024 * 1024, "MyLabel", path,
                ImageCompressionLevel.Fastest);

            DiskImageSerializer.PeekHeader(path, out _, out _, out var isEncrypted);
            Assert.False(isEncrypted);

            var loaded = DiskImageSerializer.Load(path, out var capacityBytes, out var volumeLabel, password: null, out _);
            Assert.Equal(1024UL * 1024, capacityBytes);
            Assert.Equal("MyLabel", volumeLabel);
            Assert.Equal(2, loaded.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveIncremental_MatchesV5ContentAfterRoundTrip()
    {
        var v5Path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        var v6Path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            for (var i = 0; i < 5; i++)
            {
                map.Add($"\\File{i}.txt", MakeFile(System.Text.Encoding.UTF8.GetBytes($"content for file number {i}")));
            }

            DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "Label", v5Path, ImageCompressionLevel.Fastest);
            DiskImageSerializer.SaveIncremental(map, capacityBytes: 1024 * 1024, "Label", v6Path,
                ImageCompressionLevel.Fastest);

            var v5Loaded = DiskImageSerializer.Load(v5Path, out _, out _, password: null, out _);
            var v6Loaded = DiskImageSerializer.Load(v6Path, out _, out _, password: null, out _);

            Assert.Equal(v5Loaded.Count, v6Loaded.Count);
            foreach (var kvp in v5Loaded.GetAllNodes())
            {
                Assert.True(v6Loaded.TryGet(kvp.Key, out var v6Node));
                Assert.Equal(kvp.Value.FileInfo.FileSize, v6Node!.FileInfo.FileSize);
                if (kvp.Value.FileData is not null)
                {
                    Assert.Equal(
                        kvp.Value.FileData.ToArray((long)kvp.Value.FileInfo.FileSize),
                        v6Node.FileData!.ToArray((long)v6Node.FileInfo.FileSize));
                }
            }
        }
        finally
        {
            File.Delete(v5Path);
            File.Delete(v6Path);
        }
    }

    [Fact]
    public void SaveIncremental_UpdatesSavedVersionBookkeepingOnNodes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            var file = MakeFile("hello world"u8.ToArray());
            map.Add("\\File.txt", file);

            Assert.Equal(ulong.MaxValue, file.SavedContentVersion);
            Assert.Equal(-1, file.SavedSegmentIndex);

            DiskImageSerializer.SaveIncremental(map, capacityBytes: 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest);

            Assert.Equal(file.ContentVersion, file.SavedContentVersion);
            Assert.Equal(file.MetadataVersion, file.SavedMetadataVersion);
            Assert.Equal(0, file.SavedSegmentIndex);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveIncremental_DrainsRemovedSincePersist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("hello world"u8.ToArray()));
            map.Remove("\\File.txt");

            Assert.NotEmpty(map.DrainRemovedSincePersist());

            map.Remove("\\File.txt"); // no-op, path already gone
            map.Add("\\File.txt", MakeFile("hello again"u8.ToArray()));
            map.Remove("\\File.txt");

            DiskImageSerializer.SaveIncremental(map, capacityBytes: 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest);

            Assert.Empty(map.DrainRemovedSincePersist());
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
