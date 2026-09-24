using ManagedDrive.App.Models;
using ManagedDrive.App.ViewModels;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="MainViewModel.MergeProfiles"/>, which decides which saved profiles that
/// aren't mounted still belong in the settings file alongside the mounted disks' own profiles.
/// </summary>
public sealed class MergeProfilesTests
{
    /// <summary>
    /// A not-mounted profile with no relation to any mounted disk is kept as-is.
    /// </summary>
    [Fact]
    public void MergeProfiles_UnmountedProfileWithoutConflict_KeepsIt()
    {
        var mounted = new DiskProfile { MountPoint = "R:", PersistImagePath = @"C:\a.mdr" };
        var unmounted = new DiskProfile { MountPoint = "S:", PersistImagePath = @"E:\b.mdr" };

        var merged = MainViewModel.MergeProfiles([mounted], [unmounted]);

        Assert.Equal([mounted, unmounted], merged);
    }

    /// <summary>
    /// With nothing currently mounted, every not-mounted profile is kept.
    /// </summary>
    [Fact]
    public void MergeProfiles_NoMountedDisks_KeepsEveryUnmountedProfile()
    {
        var first = new DiskProfile { MountPoint = "R:" };
        var second = new DiskProfile { MountPoint = "S:" };

        var merged = MainViewModel.MergeProfiles([], [first, second]);

        Assert.Equal([first, second], merged);
    }

    /// <summary>
    /// An auto-mount profile at the same letter as a mounted disk is superseded: keeping both
    /// would make them race for the letter on the next startup.
    /// </summary>
    [Fact]
    public void MergeProfiles_MountedDiskAtSameMountPoint_DropsUnmountedProfile()
    {
        var mounted = new DiskProfile { MountPoint = "R:" };
        var unmounted = new DiskProfile { MountPoint = "r:", AutoMount = true, PersistImagePath = @"E:\b.mdr" };

        var merged = MainViewModel.MergeProfiles([mounted], [unmounted]);

        Assert.Equal([mounted], merged);
    }

    /// <summary>
    /// A profile with auto-mount off never claims its letter, so another disk now using that
    /// letter doesn't make it drop the options remembered for its image.
    /// </summary>
    [Fact]
    public void MergeProfiles_ManualProfileWithImageAtSameMountPoint_KeepsIt()
    {
        var mounted = new DiskProfile { MountPoint = "R:", AutoMount = true };
        var unmounted = new DiskProfile { MountPoint = "r:", AutoMount = false, PersistImagePath = @"E:\b.mdr" };

        var merged = MainViewModel.MergeProfiles([mounted], [unmounted]);

        Assert.Equal([mounted, unmounted], merged);
    }

    /// <summary>
    /// A manual profile with no backing file has only its letter to identify it, so a disk at
    /// that letter supersedes it instead of a copy piling up every session.
    /// </summary>
    [Fact]
    public void MergeProfiles_ManualProfileWithoutBackingFileAtSameMountPoint_DropsIt()
    {
        var mounted = new DiskProfile { MountPoint = "R:" };
        var unmounted = new DiskProfile { MountPoint = "R:", AutoMount = false };

        var merged = MainViewModel.MergeProfiles([mounted], [unmounted]);

        Assert.Equal([mounted], merged);
    }

    /// <summary>
    /// A not-mounted profile backed by the same image as a mounted disk is superseded even at a
    /// different letter: the same disk was mounted again, possibly elsewhere.
    /// </summary>
    [Fact]
    public void MergeProfiles_MountedDiskWithSameImage_DropsUnmountedProfile()
    {
        var mounted = new DiskProfile { MountPoint = "T:", PersistImagePath = @"E:\B.mdr" };
        var unmounted = new DiskProfile { MountPoint = "R:", PersistImagePath = @"e:\b.mdr" };

        var merged = MainViewModel.MergeProfiles([mounted], [unmounted]);

        Assert.Equal([mounted], merged);
    }

    /// <summary>
    /// A not-mounted profile backed by the same source archive as a mounted disk is superseded,
    /// the same way as a matching image path.
    /// </summary>
    [Fact]
    public void MergeProfiles_MountedDiskWithSameArchive_DropsUnmountedProfile()
    {
        var mounted = new DiskProfile { MountPoint = "T:", SourceArchivePath = @"E:\x.zip" };
        var unmounted = new DiskProfile { MountPoint = "R:", SourceArchivePath = @"E:\x.zip" };

        var merged = MainViewModel.MergeProfiles([mounted], [unmounted]);

        Assert.Equal([mounted], merged);
    }

    /// <summary>
    /// Two backing-file-less profiles at different letters have nothing in common, so the
    /// not-mounted one is kept.
    /// </summary>
    [Fact]
    public void MergeProfiles_BothWithoutImagePath_KeepsUnmountedProfile()
    {
        var mounted = new DiskProfile { MountPoint = "T:" };
        var unmounted = new DiskProfile { MountPoint = "R:" };

        var merged = MainViewModel.MergeProfiles([mounted], [unmounted]);

        Assert.Equal([mounted, unmounted], merged);
    }

    /// <summary>
    /// Mounting a disk drops the not-mounted profile it supersedes immediately, rather than only
    /// while that disk stays in <see cref="MainViewModel.Disks"/> — so a profile superseded by a
    /// disk mounted outside the auto-mount loop (import, CLI mount, ...) doesn't come back once
    /// that disk is unmounted again.
    /// </summary>
    [Fact]
    public void RemoveSupersededProfiles_MountedDiskWithSameImage_RemovesSupersededProfile()
    {
        var stale = new DiskProfile { MountPoint = "R:", AutoMount = true, PersistImagePath = @"E:\b.mdr" };
        var kept = new DiskProfile { MountPoint = "S:", AutoMount = true, PersistImagePath = @"E:\other.mdr" };
        List<DiskProfile> unmounted = [stale, kept];

        MainViewModel.RemoveSupersededProfiles(unmounted, new DiskProfile { MountPoint = "T:", PersistImagePath = @"e:\B.mdr" });

        Assert.Equal([kept], unmounted);
    }
}
