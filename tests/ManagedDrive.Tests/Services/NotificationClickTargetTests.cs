using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for which balloon tip leads where in <see cref="NotificationClickTarget"/>.
/// </summary>
public sealed class NotificationClickTargetTests
{
    /// <summary>
    /// The mount point used by the tests.
    /// </summary>
    private const string MountPoint = "R:";

    /// <summary>
    /// Reports answered by changing the disk's size open the edit dialog, the others select the disk.
    /// </summary>
    /// <param name="kind">The balloon tip.</param>
    /// <param name="expected">What clicking it should lead to.</param>
    [Theory]
    [InlineData(DiskNotificationKind.HighUsage, NotificationClickKind.EditDisk)]
    [InlineData(DiskNotificationKind.CapacityAdjusted, NotificationClickKind.EditDisk)]
    [InlineData(DiskNotificationKind.SaveCompleted, NotificationClickKind.Disk)]
    [InlineData(DiskNotificationKind.SaveFailed, NotificationClickKind.Disk)]
    [InlineData(DiskNotificationKind.SaveRecovered, NotificationClickKind.Disk)]
    public void ForDisk_EachNotification_LeadsToExpectedKind(DiskNotificationKind kind, NotificationClickKind expected)
    {
        var target = NotificationClickTarget.ForDisk(kind, MountPoint);

        Assert.Equal(new NotificationClickTarget(expected, MountPoint), target);
    }

    /// <summary>
    /// A full disk opens the edit dialog; low memory does not, since a larger disk would not help.
    /// </summary>
    /// <param name="reason">Why the write was refused.</param>
    /// <param name="expected">What clicking the balloon should lead to.</param>
    [Theory]
    [InlineData(WriteRejectionReason.DiskFull, NotificationClickKind.EditDisk)]
    [InlineData(WriteRejectionReason.LowMemory, NotificationClickKind.Disk)]
    public void ForWriteRejection_EachReason_LeadsToExpectedKind(WriteRejectionReason reason, NotificationClickKind expected)
    {
        var target = NotificationClickTarget.ForWriteRejection(reason, MountPoint);

        Assert.Equal(new NotificationClickTarget(expected, MountPoint), target);
    }
}
