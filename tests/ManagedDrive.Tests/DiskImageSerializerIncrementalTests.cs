namespace ManagedDrive.Tests;

public sealed class DiskImageSerializerIncrementalTests
{
    [Fact]
    public void Save_NoExistingImage_FallsBackToFullSegmentedWrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("hello world"u8.ToArray()));

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest, encryption: null, segmentTargetBytes: 4096);

            var loaded = DiskImageSerializer.Load(path, out _, out _, password: null, out _);
            Assert.Equal(2, loaded.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_SecondSaveWithNoChanges_ProducesLoadableImageWithSameContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            for (var i = 0; i < 10; i++)
            {
                map.Add($"\\File{i}.txt", MakeFile(System.Text.Encoding.UTF8.GetBytes($"content {i}")));
            }

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest, encryption: null, segmentTargetBytes: 500);
            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest, encryption: null, segmentTargetBytes: 500);

            var loaded = DiskImageSerializer.Load(path, out _, out _, password: null, out _);
            Assert.Equal(11, loaded.Count);
            for (var i = 0; i < 10; i++)
            {
                Assert.True(loaded.TryGet($"\\File{i}.txt", out var node));
                Assert.Equal(
                    System.Text.Encoding.UTF8.GetBytes($"content {i}"),
                    node!.FileData!.ToArray((long)node.FileInfo.FileSize));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_UnchangedNodeAlongsideModifiedNode_BothLoadCorrectlyAfterSecondSave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            var untouched = MakeFile("untouched content"u8.ToArray());
            map.Add("\\Untouched.txt", untouched);
            var toModify = MakeFile("original"u8.ToArray());
            map.Add("\\Modified.txt", toModify);

            // Small target so each of the two files lands in its own segment.
            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest, encryption: null, segmentTargetBytes: 1);

            Assert.True(untouched.SavedSegmentIndex >= 0);
            var untouchedSavedContentVersionAfterFirstSave = untouched.SavedContentVersion;

            // Mutate only one file, then save again.
            toModify.FileData = FileContent.FromSpan("changed"u8.ToArray(), 512);
            toModify.FileInfo.FileSize = 7;
            toModify.ContentVersion++;

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest, encryption: null, segmentTargetBytes: 1);

            // The untouched node was never itself written to again, so its recorded "last saved
            // content version" baseline is unchanged by the modified node's rewrite.
            Assert.Equal(untouchedSavedContentVersionAfterFirstSave, untouched.SavedContentVersion);
            Assert.Equal(untouched.ContentVersion, untouched.SavedContentVersion);

            var loaded = DiskImageSerializer.Load(path, out _, out _, password: null, out _);
            Assert.True(loaded.TryGet("\\Untouched.txt", out var loadedUntouched));
            Assert.Equal(
                "untouched content"u8.ToArray(),
                loadedUntouched!.FileData!.ToArray((long)loadedUntouched.FileInfo.FileSize));

            Assert.True(loaded.TryGet("\\Modified.txt", out var loadedModified));
            Assert.Equal(
                "changed"u8.ToArray(),
                loadedModified!.FileData!.ToArray((long)loadedModified.FileInfo.FileSize));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_AfterRemovingNode_LoadedImageDoesNotContainIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\A.txt", MakeFile("a"u8.ToArray()));
            map.Add("\\B.txt", MakeFile("b"u8.ToArray()));

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.None, encryption: null, segmentTargetBytes: 100_000);

            map.Remove("\\A.txt");

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.None, encryption: null, segmentTargetBytes: 100_000);

            var loaded = DiskImageSerializer.Load(path, out _, out _, password: null, out _);
            Assert.False(loaded.TryGet("\\A.txt", out _));
            Assert.True(loaded.TryGet("\\B.txt", out _));
            Assert.Equal(2, loaded.Count); // root + B.txt
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_AfterAddingNode_LoadedImageContainsNewNode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\A.txt", MakeFile("a"u8.ToArray()));

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest, encryption: null, segmentTargetBytes: 100_000);

            map.Add("\\B.txt", MakeFile("b"u8.ToArray()));

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest, encryption: null, segmentTargetBytes: 100_000);

            var loaded = DiskImageSerializer.Load(path, out _, out _, password: null, out _);
            Assert.True(loaded.TryGet("\\A.txt", out _));
            Assert.True(loaded.TryGet("\\B.txt", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_MismatchedCompressionLevel_FallsBackToFullRewriteAndStillLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            map.Add("\\File.txt", MakeFile("hello world"u8.ToArray()));

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest, encryption: null, segmentTargetBytes: 4096);

            // Different level than the on-disk image: TryOpenOldSegmentIndex must reject reuse.
            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.SmallestSize, encryption: null, segmentTargetBytes: 4096);

            var loaded = DiskImageSerializer.Load(path, out _, out _, password: null, out _);
            Assert.True(loaded.TryGet("\\File.txt", out var node));
            Assert.Equal("hello world"u8.ToArray(), node!.FileData!.ToArray((long)node.FileInfo.FileSize));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_EncryptedIncrementalRoundTrip_LoadsWithCorrectPassword()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            var untouched = MakeFile("untouched"u8.ToArray());
            map.Add("\\Untouched.txt", untouched);
            var toModify = MakeFile("original"u8.ToArray());
            map.Add("\\Modified.txt", toModify);

            var cek = DiskImageSerializer.GenerateCek();
            var encryption = new ImageEncryptionInfo("s3cret", cek);

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest, encryption, segmentTargetBytes: 1);

            toModify.FileData = FileContent.FromSpan("changed"u8.ToArray(), 512);
            toModify.FileInfo.FileSize = 7;
            toModify.ContentVersion++;

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.Fastest, encryption, segmentTargetBytes: 1);

            var loaded = DiskImageSerializer.Load(path, out _, out _, "s3cret", out var cek2);
            Assert.NotNull(cek2);
            Assert.True(loaded.TryGet("\\Untouched.txt", out var loadedUntouched));
            Assert.Equal(
                "untouched"u8.ToArray(),
                loadedUntouched!.FileData!.ToArray((long)loadedUntouched.FileInfo.FileSize));
            Assert.True(loaded.TryGet("\\Modified.txt", out var loadedModified));
            Assert.Equal(
                "changed"u8.ToArray(),
                loadedModified!.FileData!.ToArray((long)loadedModified.FileInfo.FileSize));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_ThirdSaveReusesSegmentsWrittenByIncrementalSecondSave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            var a = MakeFile("a"u8.ToArray());
            map.Add("\\A.txt", a);
            var b = MakeFile("b"u8.ToArray());
            map.Add("\\B.txt", b);

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.None, encryption: null, segmentTargetBytes: 1);

            b.FileData = FileContent.FromSpan("bb"u8.ToArray(), 512);
            b.FileInfo.FileSize = 2;
            b.ContentVersion++;

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.None, encryption: null, segmentTargetBytes: 1);

            var aSegmentAfterSecondSave = a.SavedSegmentIndex;

            // Third save: nothing changed since the second save, so everything (including the
            // segment B was rewritten into during the second save) should now be reusable.
            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 1024 * 1024, "Label", path,
                ImageCompressionLevel.None, encryption: null, segmentTargetBytes: 1);

            Assert.Equal(aSegmentAfterSecondSave, a.SavedSegmentIndex);

            var loaded = DiskImageSerializer.Load(path, out _, out _, password: null, out _);
            Assert.True(loaded.TryGet("\\A.txt", out var loadedA));
            Assert.Equal("a"u8.ToArray(), loadedA!.FileData!.ToArray((long)loadedA.FileInfo.FileSize));
            Assert.True(loaded.TryGet("\\B.txt", out var loadedB));
            Assert.Equal("bb"u8.ToArray(), loadedB!.FileData!.ToArray((long)loadedB.FileInfo.FileSize));
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
