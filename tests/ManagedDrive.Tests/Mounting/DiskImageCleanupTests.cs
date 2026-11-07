namespace ManagedDrive.Tests;

public sealed class DiskImageCleanupTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mdrive-cleanup-" + Guid.NewGuid().ToString("N"));

    public DiskImageCleanupTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp folder.
        }
    }

    private string CreateImageWithSnapshot(out string snapshotPath)
    {
        var imagePath = Path.Combine(_directory, "disk.mdr");
        File.WriteAllText(imagePath, "image");
        snapshotPath = SnapshotManager.BuildSnapshotPath(imagePath, DateTimeOffset.UtcNow);
        File.WriteAllText(snapshotPath, "snapshot");
        return imagePath;
    }

    [Fact]
    public void DeleteImageAndSnapshots_RemovesImageAndSnapshots()
    {
        var imagePath = CreateImageWithSnapshot(out var snapshotPath);

        DiskImageCleanup.DeleteImageAndSnapshots(imagePath);

        Assert.False(File.Exists(imagePath));
        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public void DeleteImageAndSnapshots_MissingImage_IsNotAnError()
    {
        var imagePath = Path.Combine(_directory, "never-saved.mdr");

        DiskImageCleanup.DeleteImageAndSnapshots(imagePath);

        Assert.False(File.Exists(imagePath));
    }

    [Fact]
    public void DeleteImageAndSnapshots_LockedSnapshot_ThrowsAndKeepsTheImage()
    {
        var imagePath = CreateImageWithSnapshot(out var snapshotPath);

        // The image is the only persistent copy of the disk, so it must survive a failed cleanup.
        using (new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<DiskImageCleanupException>(() => DiskImageCleanup.DeleteImageAndSnapshots(imagePath));

            Assert.Equal(imagePath, ex.ImagePath);
            Assert.True(File.Exists(imagePath));
        }
    }

    private string CreateSecondImageWithSnapshot(out string snapshotPath)
    {
        var imagePath = Path.Combine(_directory, "old-location.mdr");
        File.WriteAllText(imagePath, "old image");
        snapshotPath = SnapshotManager.BuildSnapshotPath(imagePath, DateTimeOffset.UtcNow);
        File.WriteAllText(snapshotPath, "old snapshot");
        return imagePath;
    }

    [Fact]
    public void DeleteImagesAndSnapshots_RemovesEveryImageAndSnapshot()
    {
        var current = CreateImageWithSnapshot(out var currentSnapshot);
        var old = CreateSecondImageWithSnapshot(out var oldSnapshot);

        DiskImageCleanup.DeleteImagesAndSnapshots([current, old]);

        Assert.All([current, currentSnapshot, old, oldSnapshot], path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public void DeleteImagesAndSnapshots_LockedSnapshotOfLaterPath_KeepsEveryImage()
    {
        var current = CreateImageWithSnapshot(out _);
        var old = CreateSecondImageWithSnapshot(out var oldSnapshot);

        // Even though the first path's snapshots are deletable, no image may go while any
        // snapshot is stuck: the images are the only persistent copies.
        using (new FileStream(oldSnapshot, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<DiskImageCleanupException>(() => DiskImageCleanup.DeleteImagesAndSnapshots([current, old]));

            Assert.Equal(old, ex.ImagePath);
            Assert.True(File.Exists(current));
            Assert.True(File.Exists(old));
        }
    }

    [Fact]
    public void DeleteImagesAndSnapshots_LockedOlderSnapshot_AbortsBeforeTouchingTheCurrentSnapshots()
    {
        var current = CreateImageWithSnapshot(out var currentSnapshot);
        var old = CreateSecondImageWithSnapshot(out var oldSnapshot);

        // Paths go last to first, so a stuck older path aborts before the current disk's snapshot
        // history (the one that matters most) is destroyed for a format that does not happen.
        using (new FileStream(oldSnapshot, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<DiskImageCleanupException>(() => DiskImageCleanup.DeleteImagesAndSnapshots([current, old]));

            Assert.True(File.Exists(currentSnapshot));
            Assert.True(File.Exists(current));
            Assert.True(File.Exists(old));
        }
    }

    [Fact]
    public void DeleteImagesAndSnapshots_LockedOlderImage_KeepsTheCurrentImage()
    {
        var current = CreateImageWithSnapshot(out _);
        var old = CreateSecondImageWithSnapshot(out _);

        // The current image is the only copy of the disk's data: it must be deleted last, so a
        // stuck older file aborts before it is touched.
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<DiskImageCleanupException>(() => DiskImageCleanup.DeleteImagesAndSnapshots([current, old]));

            Assert.Equal(old, ex.ImagePath);
            Assert.True(File.Exists(current));
        }
    }

    [Fact]
    public void DeleteImageAndSnapshots_LockedImage_Throws()
    {
        var imagePath = CreateImageWithSnapshot(out _);

        using (new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<DiskImageCleanupException>(() => DiskImageCleanup.DeleteImageAndSnapshots(imagePath));
        }
    }
}
