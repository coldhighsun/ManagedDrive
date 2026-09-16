namespace ManagedDrive.Tests;

public sealed class RamDiskSaveDecisionsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _mainImagePath;

    public RamDiskSaveDecisionsTests()
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

    [Fact]
    public void NeedsSave_DirtyFileSystem_ReturnsTrue()
    {
        Assert.True(RamDiskSaveDecisions.NeedsSave(isDirty: true, configuredPersistImagePath: "a.mdr", lastSavedImagePath: "a.mdr"));
    }

    [Fact]
    public void NeedsSave_CleanAndPersistPathUnchanged_ReturnsFalse()
    {
        Assert.False(RamDiskSaveDecisions.NeedsSave(isDirty: false, configuredPersistImagePath: "a.mdr", lastSavedImagePath: "a.mdr"));
    }

    [Fact]
    public void NeedsSave_CleanButPersistPathChanged_ReturnsTrue()
    {
        Assert.True(RamDiskSaveDecisions.NeedsSave(isDirty: false, configuredPersistImagePath: "b.mdr", lastSavedImagePath: "a.mdr"));
    }

    [Fact]
    public void NeedsExitSave_SaveOnExitDisabled_ReturnsFalseEvenWhenNeedsSaveTrue()
    {
        Assert.False(RamDiskSaveDecisions.NeedsExitSave(saveImageOnExit: false, needsSave: true));
    }

    [Fact]
    public void NeedsExitSave_SaveOnExitEnabledAndNeedsSaveTrue_ReturnsTrue()
    {
        Assert.True(RamDiskSaveDecisions.NeedsExitSave(saveImageOnExit: true, needsSave: true));
    }

    [Fact]
    public void NeedsExitSave_SaveOnExitEnabledButNeedsSaveFalse_ReturnsFalse()
    {
        Assert.False(RamDiskSaveDecisions.NeedsExitSave(saveImageOnExit: true, needsSave: false));
    }

    [Fact]
    public void IsUnchangedSinceLatestSnapshot_NoSnapshotsExist_ReturnsFalse()
    {
        var nodeMap = MakeNodeMap("\\a.txt", [1, 2, 3]);

        Assert.False(RamDiskSaveDecisions.IsUnchangedSinceLatestSnapshot(_mainImagePath, nodeMap));
    }

    [Fact]
    public void IsUnchangedSinceLatestSnapshot_LatestSnapshotMatchesCurrent_ReturnsTrue()
    {
        var nodeMap = MakeNodeMap("\\a.txt", [1, 2, 3]);
        SnapshotManager.WriteSnapshot(nodeMap, 1024, "Label", _mainImagePath, DateTimeOffset.UtcNow, ImageCompressionLevel.None);

        Assert.True(RamDiskSaveDecisions.IsUnchangedSinceLatestSnapshot(_mainImagePath, nodeMap));
    }

    [Fact]
    public void IsUnchangedSinceLatestSnapshot_CurrentDiffersFromLatestSnapshot_ReturnsFalse()
    {
        var snapshotted = MakeNodeMap("\\a.txt", [1, 2, 3]);
        SnapshotManager.WriteSnapshot(snapshotted, 1024, "Label", _mainImagePath, DateTimeOffset.UtcNow, ImageCompressionLevel.None);

        var current = MakeNodeMap("\\a.txt", [1, 2, 3]);
        current.Add("\\b.txt", MakeFile([4, 5, 6]));

        Assert.False(RamDiskSaveDecisions.IsUnchangedSinceLatestSnapshot(_mainImagePath, current));
    }

    [Fact]
    public void IsUnchangedSinceLatestSnapshot_SnapshotReadThrows_ReturnsFalse()
    {
        // Matches the snapshot naming pattern (so ListSnapshots picks it up) but its contents
        // are garbage, so parsing it throws InvalidDataException from within the try block.
        var corruptSnapshotPath = SnapshotManager.BuildSnapshotPath(_mainImagePath, DateTimeOffset.UtcNow);
        File.WriteAllBytes(corruptSnapshotPath, [1, 2, 3, 4]);
        var nodeMap = MakeNodeMap("\\a.txt", [1, 2, 3]);

        Assert.False(RamDiskSaveDecisions.IsUnchangedSinceLatestSnapshot(_mainImagePath, nodeMap));
    }

    private static FileNode MakeDir() => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
    };

    private static FileNode MakeFile(byte[] content)
    {
        var fileSize = (ulong)content.Length;
        var allocationSize = FileNode.AlignToAllocationUnit(fileSize);

        return new()
        {
            FileInfo =
            {
                FileAttributes = (uint)FileAttributes.Normal,
                FileSize = fileSize,
                AllocationSize = allocationSize,
            },
            FileData = FileContent.FromSpan(content, allocationSize),
        };
    }

    private static FileNodeMap MakeNodeMap(string path, byte[] content)
    {
        var nodeMap = new FileNodeMap();
        nodeMap.Add("\\", MakeDir());
        nodeMap.Add(path, MakeFile(content));
        return nodeMap;
    }
}
