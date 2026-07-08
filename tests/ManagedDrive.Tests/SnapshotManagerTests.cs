using ManagedDrive.Core;

namespace ManagedDrive.Tests;

public sealed class SnapshotManagerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _mainImagePath;

    public SnapshotManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _mainImagePath = Path.Combine(_dir, "disk.mdr");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string BlobDirectory => Path.Combine(_dir, "disk.snapblobs");

    private int BlobCount => Directory.Exists(BlobDirectory)
        ? Directory.EnumerateFiles(BlobDirectory, "*.blob", SearchOption.AllDirectories).Count()
        : 0;

    [Fact]
    public void BuildSnapshotPath_AppendsSequenceOnSameSecondCollision()
    {
        var timestamp = new DateTimeOffset(2026, 7, 7, 22, 9, 0, TimeSpan.Zero);

        var first = SnapshotManager.BuildSnapshotPath(_mainImagePath, timestamp);
        File.WriteAllBytes(first, []);

        var second = SnapshotManager.BuildSnapshotPath(_mainImagePath, timestamp);
        Assert.NotEqual(first, second);
        Assert.EndsWith("-1.mdr", second);

        File.WriteAllBytes(second, []);
        var third = SnapshotManager.BuildSnapshotPath(_mainImagePath, timestamp);
        Assert.EndsWith("-2.mdr", third);
    }

    [Fact]
    public void IsSnapshotFileName_DistinguishesSnapshotsFromMainImage()
    {
        Assert.False(SnapshotManager.IsSnapshotFileName("disk.mdr"));
        Assert.True(SnapshotManager.IsSnapshotFileName("disk.20260707-220900.mdr"));
        Assert.True(SnapshotManager.IsSnapshotFileName("disk.20260707-220900-1.mdr"));
    }

    [Fact]
    public void Prune_NoLimits_IsNoOp()
    {
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", [4, 5, 6]);

        SnapshotManager.Prune(_mainImagePath, maxCount: null, maxTotalBytes: null);

        Assert.Equal(2, SnapshotManager.ListSnapshots(_mainImagePath).Count);
    }

    [Fact]
    public void Prune_CountOnly_DeletesOldestFirst()
    {
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", new byte[10]);
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", new byte[10]);
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "\\c.txt", new byte[10]);

        SnapshotManager.Prune(_mainImagePath, maxCount: 2, maxTotalBytes: null);

        var remaining = SnapshotManager.ListSnapshots(_mainImagePath);
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, s => Assert.True(s.TimestampUtc >= new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Prune_SizeOnly_DeletesUntilUnderLimit()
    {
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", new byte[100]);
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", new byte[100]);
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "\\c.txt", new byte[100]);

        // Deleting only the single oldest (100 bytes) still leaves 200 > 150, so a second
        // deletion is required to get down to 100 <= 150.
        SnapshotManager.Prune(_mainImagePath, maxCount: null, maxTotalBytes: 150);

        var remaining = SnapshotManager.ListSnapshots(_mainImagePath);
        var single = Assert.Single(remaining);
        Assert.Equal(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), single.TimestampUtc);
    }

    [Fact]
    public void Prune_BothLimits_EitherExceededTriggersCleanup()
    {
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", new byte[10]);
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", new byte[10]);
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "\\c.txt", new byte[10]);

        // Count limit of 5 is not exceeded, but the size limit of 15 is, so pruning must still
        // occur based on size alone.
        SnapshotManager.Prune(_mainImagePath, maxCount: 5, maxTotalBytes: 15);

        var remaining = SnapshotManager.ListSnapshots(_mainImagePath);
        Assert.Single(remaining);
    }

    [Fact]
    public void Prune_DoesNotTouchMainImageFile()
    {
        File.WriteAllBytes(_mainImagePath, new byte[10]);
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", new byte[10]);

        SnapshotManager.Prune(_mainImagePath, maxCount: 0, maxTotalBytes: null);

        Assert.True(File.Exists(_mainImagePath));
        Assert.Empty(SnapshotManager.ListSnapshots(_mainImagePath));
    }

    [Fact]
    public void WriteSnapshot_ThenLoadSnapshot_RoundTripsContentAndMetadata()
    {
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var nodeMap = new FileNodeMap();
        nodeMap.Add("\\", MakeDir());
        nodeMap.Add("\\Sub", MakeDir());
        nodeMap.Add("\\file.txt", MakeFile(content));
        nodeMap.Add("\\empty.txt", MakeFile([]));

        SnapshotManager.WriteSnapshot(nodeMap, 1024 * 1024, "MyLabel", _mainImagePath,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.Fastest);

        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));
        var loaded = SnapshotManager.LoadSnapshot(snapshot.Path, out var capacity, out var label);

        Assert.Equal(1024UL * 1024, capacity);
        Assert.Equal("MyLabel", label);
        Assert.Equal(4, loaded.Count);

        Assert.True(loaded.TryGet("\\file.txt", out var fileNode));
        Assert.Equal(content, fileNode!.FileData!.AsSpan(0, content.Length).ToArray());
        Assert.Equal((ulong)content.Length, fileNode.FileInfo.FileSize);

        Assert.True(loaded.TryGet("\\empty.txt", out var emptyNode));
        Assert.Equal(0UL, emptyNode!.FileInfo.FileSize);

        Assert.True(loaded.TryGet("\\Sub", out var dirNode));
        Assert.True(dirNode!.IsDirectory);
        Assert.Null(dirNode.FileData);
    }

    [Fact]
    public void WriteSnapshot_IdenticalContentAcrossTwoSnapshots_SharesOneBlob()
    {
        var content = new byte[] { 9, 9, 9 };

        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", content);
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", content);

        Assert.Equal(1, BlobCount);
    }

    [Fact]
    public void WriteSnapshot_DifferingContent_CreatesSeparateBlobs()
    {
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", [4, 5, 6]);

        Assert.Equal(2, BlobCount);
    }

    [Fact]
    public void Prune_DeletesUnreferencedBlob_KeepsBlobStillReferencedByAnotherSnapshot()
    {
        var shared = new byte[] { 1, 1, 1 };
        var unique = new byte[] { 2, 2, 2 };

        var nodeMapA = new FileNodeMap();
        nodeMapA.Add("\\", MakeDir());
        nodeMapA.Add("\\shared.txt", MakeFile(shared));
        nodeMapA.Add("\\unique.txt", MakeFile(unique));
        SnapshotManager.WriteSnapshot(nodeMapA, 1024, "Label", _mainImagePath,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.None);

        var nodeMapB = new FileNodeMap();
        nodeMapB.Add("\\", MakeDir());
        nodeMapB.Add("\\shared.txt", MakeFile(shared));
        SnapshotManager.WriteSnapshot(nodeMapB, 1024, "Label", _mainImagePath,
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.None);

        Assert.Equal(2, BlobCount);

        // Delete only the oldest snapshot (A); B still references "shared".
        SnapshotManager.Prune(_mainImagePath, maxCount: 1, maxTotalBytes: null);

        Assert.Equal(1, BlobCount);

        var remaining = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));
        var loaded = SnapshotManager.LoadSnapshot(remaining.Path, out _, out _);
        Assert.True(loaded.TryGet("\\shared.txt", out _));
    }

    [Fact]
    public void Prune_DeletingAllSnapshots_GcsAllBlobs()
    {
        var shared = new byte[] { 7, 7, 7 };
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", shared);
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", shared);

        Assert.Equal(1, BlobCount);

        SnapshotManager.Prune(_mainImagePath, maxCount: 0, maxTotalBytes: null);

        Assert.Equal(0, BlobCount);
    }

    [Fact]
    public void LoadSnapshot_MissingBlob_ReturnsClearError()
    {
        WriteSnapshotWithFile(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);

        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));

        var blobFile = Directory.EnumerateFiles(BlobDirectory, "*.blob", SearchOption.AllDirectories).Single();
        File.Delete(blobFile);

        Assert.Throws<InvalidDataException>(() => SnapshotManager.LoadSnapshot(snapshot.Path, out _, out _));
    }

    [Fact]
    public void WriteSnapshot_EmptyFile_RoundTripsWithoutBlob()
    {
        var nodeMap = new FileNodeMap();
        nodeMap.Add("\\", MakeDir());
        nodeMap.Add("\\empty.txt", MakeFile([]));

        SnapshotManager.WriteSnapshot(nodeMap, 1024, "Label", _mainImagePath,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.Fastest);

        Assert.Equal(0, BlobCount);

        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));
        var loaded = SnapshotManager.LoadSnapshot(snapshot.Path, out _, out _);
        Assert.True(loaded.TryGet("\\empty.txt", out var node));
        Assert.Equal(0UL, node!.FileInfo.FileSize);
    }

    private void WriteSnapshotWithFile(DateTimeOffset timestampUtc, string path, byte[] content)
    {
        var nodeMap = new FileNodeMap();
        nodeMap.Add("\\", MakeDir());
        nodeMap.Add(path, MakeFile(content));
        SnapshotManager.WriteSnapshot(nodeMap, 1024, "Label", _mainImagePath, timestampUtc, ImageCompressionLevel.None);
    }

    private static FileNode MakeDir() => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
    };

    private static FileNode MakeFile(byte[] content)
    {
        var fileSize = (ulong)content.Length;
        var allocationSize = FileNode.AlignToAllocationUnit(fileSize);
        var data = new byte[allocationSize];
        Buffer.BlockCopy(content, 0, data, 0, content.Length);

        return new FileNode
        {
            FileInfo =
            {
                FileAttributes = (uint)FileAttributes.Normal,
                FileSize = fileSize,
                AllocationSize = allocationSize,
            },
            FileData = data,
        };
    }
}
