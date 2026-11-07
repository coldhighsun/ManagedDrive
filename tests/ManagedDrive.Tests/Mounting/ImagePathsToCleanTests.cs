namespace ManagedDrive.Tests;

public sealed class ImagePathsToCleanTests
{
    [Fact]
    public void NoConfiguredPath_CleansNothing()
    {
        Assert.Empty(RamDiskSaveDecisions.ImagePathsToClean(null, @"C:\old.mdr", null));
    }

    [Fact]
    public void NeverSaved_CleansOnlyTheConfiguredPath()
    {
        Assert.Equal([@"C:\disk.mdr"], RamDiskSaveDecisions.ImagePathsToClean(@"C:\disk.mdr", null, null));
    }

    [Fact]
    public void SamePathAsLastSaved_IsListedOnce()
    {
        Assert.Equal([@"C:\disk.mdr"], RamDiskSaveDecisions.ImagePathsToClean(@"C:\disk.mdr", @"C:\disk.mdr", null));
    }

    [Theory]
    [InlineData(@"c:\DISK.mdr")]
    [InlineData(@"C:\other\..\disk.mdr")]
    public void SameFileSpelledDifferently_IsListedOnce(string lastSaved)
    {
        Assert.Equal([@"C:\disk.mdr"], RamDiskSaveDecisions.ImagePathsToClean(@"C:\disk.mdr", lastSaved, null));
    }

    [Fact]
    public void PathChangedSinceLastSave_CleansBothFiles_ConfiguredFirst()
    {
        Assert.Equal(
            [@"C:\new.mdr", @"C:\old.mdr"],
            RamDiskSaveDecisions.ImagePathsToClean(@"C:\new.mdr", @"C:\old.mdr", []));
    }

    [Fact]
    public void OldFileUsedByAnotherDisk_IsLeftAlone()
    {
        Assert.Equal(
            [@"C:\new.mdr"],
            RamDiskSaveDecisions.ImagePathsToClean(@"C:\new.mdr", @"C:\old.mdr", [@"C:\x.mdr", @"c:\OLD.mdr"]));
    }

    [Fact]
    public void Confirmed_OnlyConfirmedFilesAreKept()
    {
        Assert.Equal(
            [@"C:\new.mdr"],
            RamDiskSaveDecisions.ConfirmedImagePathsToClean(@"C:\new.mdr", @"C:\old.mdr", [], [@"c:\NEW.mdr"]));
    }

    [Fact]
    public void Confirmed_OldFileUnconfirmed_IsNotDeleted()
    {
        // E.g. a disk was unmounted while the dialog was open, freeing the old file: it was not
        // in what the user confirmed, so it stays.
        Assert.Empty(RamDiskSaveDecisions.ConfirmedImagePathsToClean(@"C:\new.mdr", @"C:\old.mdr", [], []));
    }

    [Fact]
    public void Confirmed_FileAnotherDiskNowUses_IsNotDeletedEvenIfConfirmed()
    {
        Assert.Equal(
            [@"C:\new.mdr"],
            RamDiskSaveDecisions.ConfirmedImagePathsToClean(
                @"C:\new.mdr", @"C:\old.mdr", [@"C:\old.mdr"], [@"C:\new.mdr", @"C:\old.mdr"]));
    }

    [Fact]
    public void Unconfirmed_ExistingConfiguredFileNotInTheConfirmation_StopsTheFormat()
    {
        Assert.True(RamDiskSaveDecisions.ConfiguredImageIsUnconfirmed(@"C:\b.mdr", true, [@"C:\a.mdr"]));
    }

    [Theory]
    [InlineData(true, @"c:\B.mdr")]
    [InlineData(false, @"C:\a.mdr")]
    public void Unconfirmed_ConfirmedOrMissingFile_DoesNotStopTheFormat(bool fileExists, string confirmed)
    {
        Assert.False(RamDiskSaveDecisions.ConfiguredImageIsUnconfirmed(@"C:\b.mdr", fileExists, [confirmed]));
    }

    [Fact]
    public void Unconfirmed_NoConfiguredPath_DoesNotStopTheFormat()
    {
        Assert.False(RamDiskSaveDecisions.ConfiguredImageIsUnconfirmed(null, true, []));
    }

    [Fact]
    public void OtherDisksWithUnrelatedPaths_DoNotHideTheOldFile()
    {
        Assert.Equal(
            [@"C:\new.mdr", @"C:\old.mdr"],
            RamDiskSaveDecisions.ImagePathsToClean(@"C:\new.mdr", @"C:\old.mdr", [@"C:\x.mdr"]));
    }
}
