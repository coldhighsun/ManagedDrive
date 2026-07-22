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

    private int BlobCount => Directory.Exists(BlobDirectory)
        ? Directory.EnumerateFiles(BlobDirectory, "*.blob", SearchOption.AllDirectories).Count()
        : 0;

    private string BlobDirectory => Path.Combine(_dir, "disk.snapblobs");

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

    [Fact]
    public void IsSnapshotFileName_DistinguishesSnapshotsFromMainImage()
    {
        Assert.False(SnapshotManager.IsSnapshotFileName("disk.mdr"));
        Assert.True(SnapshotManager.IsSnapshotFileName("disk.20260707-220900.mdr"));
        Assert.True(SnapshotManager.IsSnapshotFileName("disk.20260707-220900-1.mdr"));
    }

    [Fact]
    public void LoadSnapshot_EncryptedBlobWithoutCek_ThrowsPasswordRequired()
    {
        var cek = DiskImageSerializer.GenerateCek();
        var nodeMap = new FileNodeMap();
        nodeMap.Add("\\", MakeDir());
        nodeMap.Add("\\a.txt", MakeFile([1, 2, 3]));
        SnapshotManager.WriteSnapshot(nodeMap, 1024, "Label", _mainImagePath,
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.Fastest, cek);

        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));

        Assert.Throws<ImagePasswordRequiredException>(() =>
            SnapshotManager.LoadSnapshot(snapshot.Path, out _, out _, cek: null));
    }

    [Fact]
    public void LoadSnapshot_MissingBlob_ReturnsClearError()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);

        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));

        var blobFile = Directory.EnumerateFiles(BlobDirectory, "*.blob", SearchOption.AllDirectories).Single();
        File.Delete(blobFile);

        Assert.Throws<InvalidDataException>(() => SnapshotManager.LoadSnapshot(snapshot.Path, out _, out _));
    }

    [Fact]
    public void Prune_BothLimits_EitherExceededTriggersCleanup()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", new byte[10]);
        WriteSnapshotWithFile(new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", new byte[10]);
        WriteSnapshotWithFile(new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "\\c.txt", new byte[10]);

        // Count limit of 5 is not exceeded, but the size limit of 15 is, so pruning must still
        // occur based on size alone.
        SnapshotManager.Prune(_mainImagePath, maxCount: 5, maxTotalBytes: 15);

        var remaining = SnapshotManager.ListSnapshots(_mainImagePath);
        Assert.Single(remaining);
    }

    [Fact]
    public void Prune_CountOnly_DeletesOldestFirst()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", new byte[10]);
        WriteSnapshotWithFile(new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", new byte[10]);
        WriteSnapshotWithFile(new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "\\c.txt", new byte[10]);

        SnapshotManager.Prune(_mainImagePath, maxCount: 2, maxTotalBytes: null);

        var remaining = SnapshotManager.ListSnapshots(_mainImagePath);
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, s => Assert.True(s.TimestampUtc >= new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)));
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
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.None);

        var nodeMapB = new FileNodeMap();
        nodeMapB.Add("\\", MakeDir());
        nodeMapB.Add("\\shared.txt", MakeFile(shared));
        SnapshotManager.WriteSnapshot(nodeMapB, 1024, "Label", _mainImagePath,
            new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.None);

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
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", shared);
        WriteSnapshotWithFile(new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", shared);

        Assert.Equal(1, BlobCount);

        SnapshotManager.Prune(_mainImagePath, maxCount: 0, maxTotalBytes: null);

        Assert.Equal(0, BlobCount);
    }

    [Fact]
    public void Prune_DoesNotTouchMainImageFile()
    {
        File.WriteAllBytes(_mainImagePath, new byte[10]);
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", new byte[10]);

        SnapshotManager.Prune(_mainImagePath, maxCount: 0, maxTotalBytes: null);

        Assert.True(File.Exists(_mainImagePath));
        Assert.Empty(SnapshotManager.ListSnapshots(_mainImagePath));
    }

    [Fact]
    public void Prune_NoLimits_IsNoOp()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);
        WriteSnapshotWithFile(new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", [4, 5, 6]);

        SnapshotManager.Prune(_mainImagePath, maxCount: null, maxTotalBytes: null);

        Assert.Equal(2, SnapshotManager.ListSnapshots(_mainImagePath).Count);
    }

    [Fact]
    public void Prune_SizeOnly_DeletesUntilUnderLimit()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", new byte[100]);
        WriteSnapshotWithFile(new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", new byte[100]);
        WriteSnapshotWithFile(new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "\\c.txt", new byte[100]);

        // Deleting only the single oldest (100 bytes) still leaves 200 > 150, so a second
        // deletion is required to get down to 100 <= 150.
        SnapshotManager.Prune(_mainImagePath, maxCount: null, maxTotalBytes: 150);

        var remaining = SnapshotManager.ListSnapshots(_mainImagePath);
        var single = Assert.Single(remaining);
        Assert.Equal(new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), single.TimestampUtc);
    }

    [Fact]
    public void DeleteSnapshot_RemovesOnlyTargetIndex_LeavesOthersIntact()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);
        WriteSnapshotWithFile(new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", [4, 5, 6]);
        WriteSnapshotWithFile(new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "\\c.txt", [7, 8, 9]);

        var snapshots = SnapshotManager.ListSnapshots(_mainImagePath);
        var target = snapshots[1];

        SnapshotManager.DeleteSnapshot(_mainImagePath, target.Path);

        Assert.False(File.Exists(target.Path));
        var remaining = SnapshotManager.ListSnapshots(_mainImagePath);
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, s => s.Path == target.Path);
    }

    [Fact]
    public void DeleteSnapshot_SharedBlobStillReferencedByRemainingSnapshot_IsNotGarbageCollected()
    {
        var shared = new byte[] { 1, 1, 1 };
        var unique = new byte[] { 2, 2, 2 };

        var nodeMapA = new FileNodeMap();
        nodeMapA.Add("\\", MakeDir());
        nodeMapA.Add("\\shared.txt", MakeFile(shared));
        nodeMapA.Add("\\unique.txt", MakeFile(unique));
        SnapshotManager.WriteSnapshot(nodeMapA, 1024, "Label", _mainImagePath,
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.None);

        var nodeMapB = new FileNodeMap();
        nodeMapB.Add("\\", MakeDir());
        nodeMapB.Add("\\shared.txt", MakeFile(shared));
        SnapshotManager.WriteSnapshot(nodeMapB, 1024, "Label", _mainImagePath,
            new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.None);

        Assert.Equal(2, BlobCount);

        var oldest = SnapshotManager.ListSnapshots(_mainImagePath)[0];
        SnapshotManager.DeleteSnapshot(_mainImagePath, oldest.Path);

        Assert.Equal(1, BlobCount);
        var remaining = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));
        var loaded = SnapshotManager.LoadSnapshot(remaining.Path, out _, out _);
        Assert.True(loaded.TryGet("\\shared.txt", out _));
    }

    [Fact]
    public void DeleteSnapshot_OrphanedBlob_IsGarbageCollectedWhenNoRemainingSnapshotReferencesIt()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [3, 3, 3]);
        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));

        Assert.Equal(1, BlobCount);

        SnapshotManager.DeleteSnapshot(_mainImagePath, snapshot.Path);

        Assert.Equal(0, BlobCount);
    }

    [Fact]
    public void DeleteSnapshot_NonExistentPath_IsNoOp()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);

        var neverWritten = SnapshotManager.BuildSnapshotPath(_mainImagePath, new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        SnapshotManager.DeleteSnapshot(_mainImagePath, neverWritten);

        Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));
        Assert.Equal(1, BlobCount);
    }

    [Fact]
    public void DeleteSnapshot_InvalidSnapshotFileName_Throws()
    {
        var invalidPath = Path.Combine(_dir, "not-a-snapshot.txt");

        Assert.Throws<ArgumentException>(() => SnapshotManager.DeleteSnapshot(_mainImagePath, invalidPath));
    }

    [Fact]
    public void WriteSnapshot_DifferingContent_CreatesSeparateBlobs()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);
        WriteSnapshotWithFile(new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", [4, 5, 6]);

        Assert.Equal(2, BlobCount);
    }

    [Fact]
    public void WriteSnapshot_EmptyFile_RoundTripsWithoutBlob()
    {
        var nodeMap = new FileNodeMap();
        nodeMap.Add("\\", MakeDir());
        nodeMap.Add("\\empty.txt", MakeFile([]));

        SnapshotManager.WriteSnapshot(nodeMap, 1024, "Label", _mainImagePath,
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.Fastest);

        Assert.Equal(0, BlobCount);

        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));
        var loaded = SnapshotManager.LoadSnapshot(snapshot.Path, out _, out _);
        Assert.True(loaded.TryGet("\\empty.txt", out var node));
        Assert.Equal(0UL, node!.FileInfo.FileSize);
    }

    [Fact]
    public void WriteSnapshot_Encrypted_RoundTripsAndDedupesAcrossPasswordChange()
    {
        var cek = DiskImageSerializer.GenerateCek();
        var shared = new byte[] { 3, 1, 4, 1, 5 };

        var nodeMapA = new FileNodeMap();
        nodeMapA.Add("\\", MakeDir());
        nodeMapA.Add("\\a.txt", MakeFile(shared));
        SnapshotManager.WriteSnapshot(nodeMapA, 1024, "Label", _mainImagePath,
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.Fastest, cek);

        // Same CEK, different point in time (simulating a password change that only re-wraps the
        // CEK without touching it): identical content must still dedupe to the existing blob.
        var nodeMapB = new FileNodeMap();
        nodeMapB.Add("\\", MakeDir());
        nodeMapB.Add("\\b.txt", MakeFile(shared));
        SnapshotManager.WriteSnapshot(nodeMapB, 1024, "Label", _mainImagePath,
            new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.Fastest, cek);

        Assert.Equal(1, BlobCount);

        var snapshots = SnapshotManager.ListSnapshots(_mainImagePath);
        Assert.Equal(2, snapshots.Count);

        foreach (var snapshot in snapshots)
        {
            var loaded = SnapshotManager.LoadSnapshot(snapshot.Path, out _, out _, cek);
            var hasA = loaded.TryGet("\\a.txt", out var nodeA);
            var hasB = loaded.TryGet("\\b.txt", out var nodeB);
            Assert.True(hasA || hasB);

            var node = hasA ? nodeA : nodeB;
            Assert.Equal(shared, node!.FileData!.AsSpan(0, shared.Length).ToArray());
        }
    }

    [Fact]
    public void WriteSnapshot_IdenticalContentAcrossTwoSnapshots_SharesOneBlob()
    {
        var content = new byte[] { 9, 9, 9 };

        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", content);
        WriteSnapshotWithFile(new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "\\b.txt", content);

        Assert.Equal(1, BlobCount);
    }

    [Fact]
    public void ListSnapshotContents_ReturnsAllPathsWithSizes()
    {
        var nodeMap = new FileNodeMap();
        nodeMap.Add("\\", MakeDir());
        nodeMap.Add("\\Sub", MakeDir());
        nodeMap.Add("\\a.txt", MakeFile([1, 2, 3]));
        SnapshotManager.WriteSnapshot(nodeMap, 1024, "Label", _mainImagePath,
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.None);

        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));
        var entries = SnapshotManager.ListSnapshotContents(snapshot.Path);

        Assert.Contains(entries, e => e.Path == "\\" && e.IsDirectory);
        Assert.Contains(entries, e => e.Path == "\\Sub" && e.IsDirectory);
        Assert.Contains(entries, e => e.Path == "\\a.txt" && !e.IsDirectory && e.SizeBytes == 3);
    }

    [Fact]
    public void DiffAgainstCurrent_DetectsAddedFile()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);
        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));

        var current = new FileNodeMap();
        current.Add("\\", MakeDir());
        current.Add("\\a.txt", MakeFile([1, 2, 3]));
        current.Add("\\b.txt", MakeFile([4, 5, 6]));

        var diff = SnapshotManager.DiffAgainstCurrent(snapshot.Path, current);

        Assert.Equal(["\\b.txt"], diff.AddedFiles);
        Assert.Empty(diff.RemovedFiles);
        Assert.Empty(diff.ModifiedFiles);
        Assert.Equal(1, diff.UnchangedFileCount);
    }

    [Fact]
    public void DiffAgainstCurrent_DetectsRemovedFile()
    {
        var nodeMap = new FileNodeMap();
        nodeMap.Add("\\", MakeDir());
        nodeMap.Add("\\a.txt", MakeFile([1, 2, 3]));
        nodeMap.Add("\\b.txt", MakeFile([4, 5, 6]));
        SnapshotManager.WriteSnapshot(nodeMap, 1024, "Label", _mainImagePath,
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.None);
        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));

        var current = new FileNodeMap();
        current.Add("\\", MakeDir());
        current.Add("\\a.txt", MakeFile([1, 2, 3]));

        var diff = SnapshotManager.DiffAgainstCurrent(snapshot.Path, current);

        Assert.Equal(["\\b.txt"], diff.RemovedFiles);
        Assert.Empty(diff.AddedFiles);
        Assert.Empty(diff.ModifiedFiles);
        Assert.Equal(1, diff.UnchangedFileCount);
    }

    [Fact]
    public void DiffAgainstCurrent_DetectsModifiedFile()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);
        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));

        var current = new FileNodeMap();
        current.Add("\\", MakeDir());
        current.Add("\\a.txt", MakeFile([9, 9, 9]));

        var diff = SnapshotManager.DiffAgainstCurrent(snapshot.Path, current);

        Assert.Equal(["\\a.txt"], diff.ModifiedFiles);
        Assert.Empty(diff.AddedFiles);
        Assert.Empty(diff.RemovedFiles);
        Assert.Equal(0, diff.UnchangedFileCount);
    }

    [Fact]
    public void DiffAgainstCurrent_UnchangedFile_NotListedAsModified()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);
        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));

        var current = new FileNodeMap();
        current.Add("\\", MakeDir());
        current.Add("\\a.txt", MakeFile([1, 2, 3]));

        var diff = SnapshotManager.DiffAgainstCurrent(snapshot.Path, current);

        Assert.Empty(diff.AddedFiles);
        Assert.Empty(diff.RemovedFiles);
        Assert.Empty(diff.ModifiedFiles);
        Assert.Equal(1, diff.UnchangedFileCount);
    }

    [Fact]
    public void DiffAgainstCurrent_IdenticalContents_HasChangesIsFalse()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);
        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));

        var current = new FileNodeMap();
        current.Add("\\", MakeDir());
        current.Add("\\a.txt", MakeFile([1, 2, 3]));

        var diff = SnapshotManager.DiffAgainstCurrent(snapshot.Path, current);

        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void DiffAgainstCurrent_AnyChangeKind_HasChangesIsTrue()
    {
        WriteSnapshotWithFile(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "\\a.txt", [1, 2, 3]);
        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));

        var current = new FileNodeMap();
        current.Add("\\", MakeDir());
        current.Add("\\a.txt", MakeFile([1, 2, 3]));
        current.Add("\\b.txt", MakeFile([4, 5, 6]));

        var diff = SnapshotManager.DiffAgainstCurrent(snapshot.Path, current);

        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void DiffAgainstCurrent_EmptyFileUnchanged_NotListedAsModified()
    {
        var nodeMap = new FileNodeMap();
        nodeMap.Add("\\", MakeDir());
        nodeMap.Add("\\empty.txt", MakeFile([]));
        SnapshotManager.WriteSnapshot(nodeMap, 1024, "Label", _mainImagePath,
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.None);
        var snapshot = Assert.Single(SnapshotManager.ListSnapshots(_mainImagePath));

        var current = new FileNodeMap();
        current.Add("\\", MakeDir());
        current.Add("\\empty.txt", MakeFile([]));

        var diff = SnapshotManager.DiffAgainstCurrent(snapshot.Path, current);

        Assert.Empty(diff.ModifiedFiles);
        Assert.Equal(1, diff.UnchangedFileCount);
    }

    [Fact]
    public void WriteSnapshot_MultipleNodes_ReportsMonotonicProgressEndingAtOne()
    {
        var nodeMap = new FileNodeMap();
        nodeMap.Add("\\", MakeDir());
        for (var i = 0; i < 5; i++)
        {
            nodeMap.Add($"\\File{i}.txt", MakeFile([(byte)i, 1, 2, 3]));
        }

        var reports = new List<double>();
        SnapshotManager.WriteSnapshot(nodeMap, 1024 * 1024, "Label", _mainImagePath,
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.Fastest,
            progress: new RecordingProgress(reports));

        Assert.NotEmpty(reports);
        Assert.True(reports[0] > 0);
        Assert.Equal(1.0, reports[^1]);
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i] >= reports[i - 1]);
        }
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
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ImageCompressionLevel.Fastest);

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

        return new()
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

    private void WriteSnapshotWithFile(DateTimeOffset timestampUtc, string path, byte[] content)
    {
        var nodeMap = new FileNodeMap();
        nodeMap.Add("\\", MakeDir());
        nodeMap.Add(path, MakeFile(content));
        SnapshotManager.WriteSnapshot(nodeMap, 1024, "Label", _mainImagePath, timestampUtc, ImageCompressionLevel.None);
    }
}