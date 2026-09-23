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

    [Theory]
    [InlineData(ImageCompressionLevel.None, false)]
    [InlineData(ImageCompressionLevel.Fastest, false)]
    [InlineData(ImageCompressionLevel.None, true)]
    [InlineData(ImageCompressionLevel.Fastest, true)]
    public void Load_ManySegments_DecodedInParallel_RoundTripsInOrder(ImageCompressionLevel level, bool encrypted)
    {
        // Far more segments than processors, so the load keeps a full window of segments in
        // flight and has to reassemble them in file order.
        var contents = Enumerable.Range(0, 200)
            .Select(i => System.Text.Encoding.UTF8.GetBytes($"content of file {i} " + new string((char)('a' + i % 26), i)))
            .ToArray();

        AssertSegmentedRoundTrip(contents, level, encrypted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_LargeSegmentsBetweenSmallOnes_RoundTripsInOrder(bool encrypted)
    {
        // With a 4 KB Zstd chunk, the random (incompressible) 20 KB files make segments whose
        // payload spans several chunks, which the load decodes on its own with full parallelism
        // after draining the small segments queued before it.
        ParallelZstd.TestChunkSizeOverride = 4096;
        try
        {
            var random = new Random(42);
            var contents = Enumerable.Range(0, 40)
                .Select(i =>
                {
                    if (i % 5 != 0)
                    {
                        return System.Text.Encoding.UTF8.GetBytes($"small file {i}");
                    }

                    var bytes = new byte[20_000 + i];
                    random.NextBytes(bytes);
                    return bytes;
                })
                .ToArray();

            AssertSegmentedRoundTrip(contents, ImageCompressionLevel.Fastest, encrypted);
        }
        finally
        {
            ParallelZstd.TestChunkSizeOverride = null;
        }
    }

    /// <summary>
    /// Saves one file per segment, loads the image, and checks every file's content plus that the
    /// loaded nodes were stamped with their segments in order: an incremental save straight after
    /// the load must find every segment clean and reproduce the image byte for byte.
    /// </summary>
    private static void AssertSegmentedRoundTrip(byte[][] contents, ImageCompressionLevel level, bool encrypted)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        try
        {
            var map = new FileNodeMap();
            map.Add("\\", MakeDir());
            for (var i = 0; i < contents.Length; i++)
            {
                map.Add($"\\File{i:D4}.bin", MakeFile(contents[i]));
            }

            const string password = "s3cret";
            var encryption = encrypted ? new ImageEncryptionInfo(password, DiskImageSerializer.GenerateCek()) : (ImageEncryptionInfo?)null;

            DiskImageSerializer.SaveSegmentedIncrementalForTest(map, 16 * 1024 * 1024, "Label", path,
                level, encryption, segmentTargetBytes: 1);
            var saved = File.ReadAllBytes(path);

            var loaded = DiskImageSerializer.Load(path, out _, out _, encrypted ? password : null, out var cek);

            Assert.Equal(contents.Length + 1, loaded.Count);
            for (var i = 0; i < contents.Length; i++)
            {
                Assert.True(loaded.TryGet($"\\File{i:D4}.bin", out var node));
                Assert.Equal(contents[i], node!.FileData!.ToArray((long)node.FileInfo.FileSize));
            }

            var segmentIndices = loaded.GetAllNodes().Select(kvp => kvp.Value.SavedSegmentIndex).ToArray();
            Assert.Equal(segmentIndices.Order(), segmentIndices);
            Assert.True(segmentIndices[^1] >= contents.Length - 1);

            var reloadEncryption = encrypted ? new ImageEncryptionInfo(password, cek!) : (ImageEncryptionInfo?)null;
            DiskImageSerializer.SaveSegmentedIncrementalForTest(loaded, 16 * 1024 * 1024, "Label", path,
                level, reloadEncryption, segmentTargetBytes: 1);
            var resaved = File.ReadAllBytes(path);

            // Every save re-wraps the CEK under a fresh salt and nonce, so for an encrypted image
            // compare from the segment index onward: magic(4) version(4) level(1) encrypted(1)
            // capacity(8) label(1 + 5), then salt(16) iterations(4) nonce(12) tag(16) wrapped CEK(32).
            var skip = encrypted ? 24 + 80 : 0;
            Assert.Equal(saved.Length, resaved.Length);
            Assert.Equal(saved.AsSpan(skip).ToArray(), resaved.AsSpan(skip).ToArray());
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
