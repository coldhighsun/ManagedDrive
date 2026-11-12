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

    [Theory]
    [InlineData("a.mdr", false, true, true, true)]
    [InlineData(null, false, true, true, false)]
    [InlineData("a.mdr", true, true, true, false)]
    [InlineData("a.mdr", false, false, true, false)]
    [InlineData("a.mdr", false, true, false, false)]
    public void WillSaveOnDispose_RequiresTargetNotSkippedExitSaveAndChanges(
        string? path, bool skip, bool saveOnExit, bool needsSave, bool expected)
    {
        Assert.Equal(expected, RamDiskSaveDecisions.WillSaveOnDispose(path, skip, saveOnExit, needsSave));
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

    [Theory]
    [InlineData(5u, "a.mdr", 5u, "a.mdr", false, false)]
    [InlineData(null, null, null, null, false, false)]
    [InlineData(5u, "a.mdr", 10u, "a.mdr", false, true)]
    [InlineData(5u, "a.mdr", null, "a.mdr", false, true)]
    [InlineData(null, "a.mdr", 5u, "a.mdr", false, true)]
    [InlineData(5u, "a.mdr", 5u, "b.mdr", false, true)]
    [InlineData(5u, "a.mdr", 5u, null, false, true)]
    [InlineData(5u, "a.mdr", 5u, "a.mdr", true, true)]
    public void AutoSaveTimerNeedsRestart_ScheduleChangeOrPendingHeaderSave_ReturnsExpected(
        uint? oldInterval, string? oldPath, uint? newInterval, string? newPath, bool headerSaveNeeded, bool expected)
    {
        Assert.Equal(
            expected,
            RamDiskSaveDecisions.AutoSaveTimerNeedsRestart(
                Minutes(oldInterval), oldPath, Minutes(newInterval), newPath, headerSaveNeeded, snapshotLimitsChanged: false));
    }

    [Fact]
    public void AutoSaveTimerNeedsRestart_OnlySnapshotLimitsChanged_ReturnsTrue()
    {
        Assert.True(RamDiskSaveDecisions.AutoSaveTimerNeedsRestart(
            TimeSpan.FromMinutes(5), "a.mdr", TimeSpan.FromMinutes(5), "a.mdr", headerSaveNeeded: false, snapshotLimitsChanged: true));
    }

    /// <summary>
    /// The interval is the configured whole minutes as a <see cref="TimeSpan"/>, and <c>null</c>
    /// when auto-save is off.
    /// </summary>
    /// <param name="minutes">The configured minutes, or <c>null</c> for none.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(5u)]
    [InlineData(71_000u)]
    public void AutoSaveInterval_ConfiguredMinutes_ReturnsMatchingTimeSpan(uint? minutes)
    {
        var options = new DiskOptions { MountPoint = "R:", CapacityBytes = 1024, AutoSaveIntervalMinutes = minutes };

        Assert.Equal(Minutes(minutes), RamDiskSaveDecisions.AutoSaveInterval(options));
    }

    [Theory]
    [InlineData(null, null, null, null, false)]
    [InlineData(5u, null, 5u, null, false)]
    [InlineData(null, null, 5u, null, true)]
    [InlineData(5u, null, 3u, null, true)]
    [InlineData(null, 1024ul, null, null, true)]
    [InlineData(null, 1024ul, null, 2048ul, true)]
    public void SnapshotLimitsChanged_CountOrSizeDiffers_ReturnsExpected(
        uint? oldCount, ulong? oldSize, uint? newCount, ulong? newSize, bool expected)
    {
        var oldOptions = new DiskOptions { MountPoint = "R:", CapacityBytes = 1024, MaxSnapshotCount = oldCount, MaxSnapshotSizeBytes = oldSize };
        var newOptions = oldOptions with { MaxSnapshotCount = newCount, MaxSnapshotSizeBytes = newSize };

        Assert.Equal(expected, RamDiskSaveDecisions.SnapshotLimitsChanged(oldOptions, newOptions));
    }

    [Theory]
    [InlineData("Data", "Data", "Data", "Data")]
    [InlineData("Data", "Data", "Work", "Work")]
    [InlineData("Data", "Edited", "Data", "Edited")]
    [InlineData("Data", "Edited", "Work", "Edited")]
    public void MergeVolumeLabel_EditChangedLabelOrNot_ReturnsExpected(
        string labelBeforeEdit, string editedLabel, string currentLabel, string expected)
    {
        Assert.Equal(expected, RamDiskSaveDecisions.MergeVolumeLabel(labelBeforeEdit, editedLabel, currentLabel));
    }

    [Fact]
    public void ShouldForceFullRewrite_SamePathNoRotation_ReturnsFalse()
    {
        Assert.False(RamDiskSaveDecisions.ShouldForceFullRewrite(
            cekRotatedSinceLastSave: false, configuredPersistImagePath: "a.mdr", lastSavedImagePath: "a.mdr"));
    }

    [Fact]
    public void ShouldForceFullRewrite_CekRotated_ReturnsTrue()
    {
        Assert.True(RamDiskSaveDecisions.ShouldForceFullRewrite(
            cekRotatedSinceLastSave: true, configuredPersistImagePath: "a.mdr", lastSavedImagePath: "a.mdr"));
    }

    [Fact]
    public void ShouldForceFullRewrite_PersistPathChanged_ReturnsTrue()
    {
        // The nodes' saved-segment indices describe a.mdr's layout; reusing them against b.mdr
        // would copy whatever segments b.mdr happens to hold.
        Assert.True(RamDiskSaveDecisions.ShouldForceFullRewrite(
            cekRotatedSinceLastSave: false, configuredPersistImagePath: "b.mdr", lastSavedImagePath: "a.mdr"));
    }

    [Fact]
    public void ShouldForceFullRewrite_NeverSavedOrLoaded_ReturnsTrue()
    {
        Assert.True(RamDiskSaveDecisions.ShouldForceFullRewrite(
            cekRotatedSinceLastSave: false, configuredPersistImagePath: "a.mdr", lastSavedImagePath: null));
    }

    [Fact]
    public void ShouldGenerateNewCek_NoCurrentCek_ReturnsTrue()
    {
        // Mirrors RamDisk.SetPassword's guard: no CEK (unencrypted, or a password was removed
        // since the last save) means the caller must generate a fresh one and treat it as a
        // rotation — an existing on-disk image may still hold segments encrypted under a
        // discarded key.
        Assert.True(RamDiskSaveDecisions.ShouldGenerateNewCek(currentCek: null));
    }

    [Fact]
    public void ShouldGenerateNewCek_ExistingCek_ReturnsFalse()
    {
        // An already-encrypted disk changing its password only re-wraps the existing CEK — no
        // rotation, so segment reuse across the change remains safe.
        Assert.False(RamDiskSaveDecisions.ShouldGenerateNewCek(currentCek: [1, 2, 3]));
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
        SnapshotManager.WriteSnapshot(nodeMap, 1024, "Label", _mainImagePath, DateTimeOffset.UtcNow, 0);

        Assert.True(RamDiskSaveDecisions.IsUnchangedSinceLatestSnapshot(_mainImagePath, nodeMap));
    }

    [Fact]
    public void IsUnchangedSinceLatestSnapshot_CurrentDiffersFromLatestSnapshot_ReturnsFalse()
    {
        var snapshotted = MakeNodeMap("\\a.txt", [1, 2, 3]);
        SnapshotManager.WriteSnapshot(snapshotted, 1024, "Label", _mainImagePath, DateTimeOffset.UtcNow, 0);

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

    [Theory]
    [InlineData("a.mdr", "a.mdr", "a.mdr", true, false, true)]
    [InlineData(null, null, null, true, false, false)]
    [InlineData("a.mdr", "b.mdr", "a.mdr", true, false, false)]
    [InlineData("a.mdr", null, "a.mdr", true, false, false)]
    [InlineData("a.mdr", "a.mdr", "other.mdr", true, false, false)]
    [InlineData("a.mdr", "a.mdr", null, true, false, false)]
    [InlineData("a.mdr", "a.mdr", "a.mdr", false, false, false)]
    [InlineData("a.mdr", "a.mdr", "a.mdr", true, true, false)]
    public void CanPatchImageCapacity_VariousStates_ReturnsExpected(
        string? currentPath,
        string? newPath,
        string? lastSavedPath,
        bool saveImageOnExit,
        bool readOnly,
        bool expected)
    {
        var result = RamDiskSaveDecisions.CanPatchImageCapacity(
            currentPath, newPath, lastSavedPath, saveImageOnExit, readOnly);

        Assert.Equal(expected, result);
    }

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

    /// <summary>
    /// Converts whole minutes from test data, which cannot hold a <see cref="TimeSpan"/>.
    /// </summary>
    /// <param name="minutes">The minutes, or <c>null</c>.</param>
    /// <returns>The interval, or <c>null</c>.</returns>
    private static TimeSpan? Minutes(uint? minutes) => minutes is { } m ? TimeSpan.FromMinutes(m) : null;
}
