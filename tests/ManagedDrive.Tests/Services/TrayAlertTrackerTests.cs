using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests when <see cref="TrayAlertTracker"/> starts and ends each tray alert.
/// </summary>
public sealed class TrayAlertTrackerTests
{
    /// <summary>
    /// One mebibyte.
    /// </summary>
    private const ulong Mb = 1024UL * 1024;

    /// <summary>
    /// A fixed point in time the tests count from.
    /// </summary>
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Plenty of available memory.
    /// </summary>
    private static readonly ulong PlentyOfMemory = LowMemoryMonitor.RecoverAboveBytes + 1;

    /// <summary>
    /// Verifies that a full-disk refusal is a new critical alert of that disk.
    /// </summary>
    [Fact]
    public void OnWriteRejected_DiskFull_RaisesNewCritical()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        var disk = new object();

        // Act
        var update = tracker.OnWriteRejected(disk, lowMemory: false, Start);

        // Assert
        Assert.True(update.NewCritical);
        Assert.Equal([disk], update.ChangedKeys);
        Assert.Equal(TrayAlertReasons.DiskFull, tracker.Get(disk));
        Assert.Equal(TrayAlertLevel.Critical, tracker.Level);
    }

    /// <summary>
    /// Verifies that a second refusal of the same kind on the same disk is not a new failure.
    /// </summary>
    [Fact]
    public void OnWriteRejected_SameReasonAgain_NoNewCritical()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        var disk = new object();
        tracker.OnWriteRejected(disk, lowMemory: false, Start);

        // Act
        var update = tracker.OnWriteRejected(disk, lowMemory: false, Start + TimeSpan.FromSeconds(5));

        // Assert
        Assert.False(update.HasChanges);
    }

    /// <summary>
    /// Verifies that a failure on a second disk is new even though the level already is critical.
    /// </summary>
    [Fact]
    public void OnSaveFailed_OtherDiskWhileCritical_RaisesNewCritical()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        tracker.OnWriteRejected(new object(), lowMemory: false, Start);

        // Act
        var update = tracker.OnSaveFailed(new object());

        // Assert
        Assert.True(update.NewCritical);
    }

    /// <summary>
    /// Verifies that the full alert lasts the whole hold, so freeing space right after the refusal (a
    /// failed copy deleting its partial file) does not end it.
    /// </summary>
    [Fact]
    public void Expire_WithinTheHold_KeepsTheFullAlert()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        var disk = new object();
        tracker.OnWriteRejected(disk, lowMemory: false, Start);

        // Act
        var update = tracker.Expire(Start + TrayAlertTracker.RefusalHold - TimeSpan.FromSeconds(1));

        // Assert
        Assert.False(update.HasChanges);
        Assert.Equal(TrayAlertReasons.DiskFull, tracker.Get(disk));
    }

    /// <summary>
    /// Verifies that the full alert ends once the hold has passed without a new refusal.
    /// </summary>
    [Fact]
    public void Expire_AfterTheHold_EndsTheFullAlert()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        var disk = new object();
        tracker.OnWriteRejected(disk, lowMemory: false, Start);

        // Act
        var update = tracker.Expire(Start + TrayAlertTracker.RefusalHold);

        // Assert
        Assert.Equal([disk], update.ChangedKeys);
        Assert.Equal(TrayAlertReasons.None, tracker.Get(disk));
        Assert.Equal(TrayAlertLevel.None, tracker.Level);
    }

    /// <summary>
    /// Verifies that a new refusal starts the hold again.
    /// </summary>
    [Fact]
    public void Expire_RefusedAgainDuringTheHold_HoldStartsAgain()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        var disk = new object();
        tracker.OnWriteRejected(disk, lowMemory: false, Start);
        var again = Start + TimeSpan.FromSeconds(50);
        tracker.OnWriteRejected(disk, lowMemory: false, again);

        // Act
        tracker.Expire(Start + TrayAlertTracker.RefusalHold);

        // Assert
        Assert.Equal(TrayAlertReasons.DiskFull, tracker.Get(disk));
        tracker.Expire(again + TrayAlertTracker.RefusalHold);
        Assert.Equal(TrayAlertReasons.None, tracker.Get(disk));
    }

    /// <summary>
    /// Verifies that a low-memory refusal outlasts the hold while memory has not recovered.
    /// </summary>
    [Fact]
    public void OnMemoryReading_MemoryStillLowAfterTheHold_KeepsTheRefusal()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        var disk = new object();
        tracker.OnWriteRejected(disk, lowMemory: true, Start);

        // Act
        tracker.OnMemoryReading(600 * Mb, 1, Start + TrayAlertTracker.RefusalHold * 2);

        // Assert
        Assert.True(tracker.Get(disk).HasFlag(TrayAlertReasons.MemoryRejected));
    }

    /// <summary>
    /// Verifies that a low-memory refusal ends once the hold has passed and memory has recovered.
    /// </summary>
    [Fact]
    public void OnMemoryReading_RecoveredAfterTheHold_EndsTheRefusal()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        var disk = new object();
        tracker.OnWriteRejected(disk, lowMemory: true, Start);

        // Act
        tracker.OnMemoryReading(PlentyOfMemory, 1, Start + TrayAlertTracker.RefusalHold);

        // Assert
        Assert.Equal(TrayAlertReasons.None, tracker.Get(disk));
    }

    /// <summary>
    /// Verifies that memory recovering within the hold does not end a low-memory refusal early.
    /// </summary>
    [Fact]
    public void OnMemoryReading_RecoveredWithinTheHold_KeepsTheRefusal()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        var disk = new object();
        tracker.OnWriteRejected(disk, lowMemory: true, Start);

        // Act
        tracker.OnMemoryReading(PlentyOfMemory, 1, Start + TimeSpan.FromSeconds(10));

        // Assert
        Assert.True(tracker.Get(disk).HasFlag(TrayAlertReasons.MemoryRejected));
    }

    /// <summary>
    /// Verifies the low-memory warning: on below the warn threshold with a disk mounted, kept between
    /// the thresholds, off above the recovery threshold.
    /// </summary>
    [Fact]
    public void OnMemoryReading_Hysteresis_FollowsTheThresholds()
    {
        // Arrange
        var tracker = new TrayAlertTracker();

        // Act / Assert
        tracker.OnMemoryReading(LowMemoryMonitor.WarnBelowBytes - 1, 1, Start);
        Assert.Equal(TrayAlertLevel.Warning, tracker.Level);

        tracker.OnMemoryReading(LowMemoryMonitor.RecoverAboveBytes, 1, Start);
        Assert.Equal(TrayAlertLevel.Warning, tracker.Level);

        tracker.OnMemoryReading(PlentyOfMemory, 1, Start);
        Assert.Equal(TrayAlertLevel.None, tracker.Level);
    }

    /// <summary>
    /// Verifies that low memory raises no warning while no disk is mounted.
    /// </summary>
    [Fact]
    public void OnMemoryReading_NoDiskMounted_NoWarning()
    {
        // Arrange
        var tracker = new TrayAlertTracker();

        // Act
        tracker.OnMemoryReading(100 * Mb, 0, Start);

        // Assert
        Assert.Equal(TrayAlertLevel.None, tracker.Level);
    }

    /// <summary>
    /// Verifies that removing the last disk also ends the system low-memory warning at once.
    /// </summary>
    [Fact]
    public void RemoveDisk_LastDisk_EndsTheLowMemoryWarning()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        var disk = new object();
        tracker.OnMemoryReading(100 * Mb, 1, Start);
        tracker.OnSaveFailed(disk);

        // Act
        var update = tracker.RemoveDisk(disk, 0);

        // Assert
        Assert.Contains(disk, update.ChangedKeys);
        Assert.Contains(TrayAlertTracker.SystemKey, update.ChangedKeys);
        Assert.Equal(TrayAlertLevel.None, tracker.Level);
    }

    /// <summary>
    /// Verifies that a removed disk's pending refusal does not come back on a later expiry.
    /// </summary>
    [Fact]
    public void RemoveDisk_WithPendingRefusal_ForgetsIt()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        var disk = new object();
        tracker.OnWriteRejected(disk, lowMemory: false, Start);

        // Act
        tracker.RemoveDisk(disk, 1);
        var update = tracker.Expire(Start + TrayAlertTracker.RefusalHold);

        // Assert
        Assert.False(update.HasChanges);
    }

    /// <summary>
    /// Verifies that a failed save lasts until a save succeeds, not for a hold.
    /// </summary>
    [Fact]
    public void OnSaveRecovered_AfterFailure_EndsTheAlert()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        var disk = new object();
        tracker.OnSaveFailed(disk);
        tracker.Expire(Start + TimeSpan.FromHours(1));
        Assert.Equal(TrayAlertReasons.SaveFailed, tracker.Get(disk));

        // Act
        var update = tracker.OnSaveRecovered(disk);

        // Assert
        Assert.False(update.NewCritical);
        Assert.Equal(TrayAlertReasons.None, tracker.Get(disk));
    }

    /// <summary>
    /// Verifies that high usage is a warning, not a new critical failure.
    /// </summary>
    [Fact]
    public void SetHighUsage_On_RaisesWarningOnly()
    {
        // Arrange
        var tracker = new TrayAlertTracker();
        var disk = new object();

        // Act
        var update = tracker.SetHighUsage(disk, true);

        // Assert
        Assert.False(update.NewCritical);
        Assert.Equal(TrayAlertLevel.Warning, tracker.Level);
    }
}
