using ManagedDrive.App.Models;
using ManagedDrive.App.ViewModels;

namespace ManagedDrive.Tests;

public sealed class MergeProfilesTests
{
    [Fact]
    public void MergeProfiles_UnmountedProfileWithoutConflict_KeepsIt()
    {
        var mounted = new DiskProfile { MountPoint = "R:", PersistImagePath = @"C:\a.mdr" };
        var unmounted = new DiskProfile { MountPoint = "S:", PersistImagePath = @"E:\b.mdr" };

        var merged = MainViewModel.MergeProfiles([mounted], [unmounted]);

        Assert.Equal([mounted, unmounted], merged);
    }

    [Fact]
    public void MergeProfiles_NoMountedDisks_KeepsEveryUnmountedProfile()
    {
        var first = new DiskProfile { MountPoint = "R:" };
        var second = new DiskProfile { MountPoint = "S:" };

        var merged = MainViewModel.MergeProfiles([], [first, second]);

        Assert.Equal([first, second], merged);
    }

    [Fact]
    public void MergeProfiles_MountedDiskAtSameMountPoint_DropsUnmountedProfile()
    {
        var mounted = new DiskProfile { MountPoint = "R:" };
        var unmounted = new DiskProfile { MountPoint = "r:", PersistImagePath = @"E:\b.mdr" };

        var merged = MainViewModel.MergeProfiles([mounted], [unmounted]);

        Assert.Equal([mounted], merged);
    }

    [Fact]
    public void MergeProfiles_MountedDiskWithSameImage_DropsUnmountedProfile()
    {
        var mounted = new DiskProfile { MountPoint = "T:", PersistImagePath = @"E:\B.mdr" };
        var unmounted = new DiskProfile { MountPoint = "R:", PersistImagePath = @"e:\b.mdr" };

        var merged = MainViewModel.MergeProfiles([mounted], [unmounted]);

        Assert.Equal([mounted], merged);
    }

    [Fact]
    public void MergeProfiles_MountedDiskWithSameArchive_DropsUnmountedProfile()
    {
        var mounted = new DiskProfile { MountPoint = "T:", SourceArchivePath = @"E:\x.zip" };
        var unmounted = new DiskProfile { MountPoint = "R:", SourceArchivePath = @"E:\x.zip" };

        var merged = MainViewModel.MergeProfiles([mounted], [unmounted]);

        Assert.Equal([mounted], merged);
    }

    [Fact]
    public void MergeProfiles_BothWithoutImagePath_KeepsUnmountedProfile()
    {
        var mounted = new DiskProfile { MountPoint = "T:" };
        var unmounted = new DiskProfile { MountPoint = "R:" };

        var merged = MainViewModel.MergeProfiles([mounted], [unmounted]);

        Assert.Equal([mounted, unmounted], merged);
    }
}
