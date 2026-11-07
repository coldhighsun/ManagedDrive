using ManagedDrive.App.Localization;
using ManagedDrive.App.ViewModels;

namespace ManagedDrive.Tests;

public sealed class MainViewModelFormatReplyTests
{
    [Fact]
    public void Split_ConfiguredImageIsReset_PreviousImageIsDeleted()
    {
        var result = new FormatResult(true, [@"C:\new.mdr", @"C:\old.mdr"]);

        var (reset, deleted) = MainViewModel.SplitCleanedImagePaths(@"c:\NEW.mdr", result);

        Assert.Equal([@"C:\new.mdr"], reset);
        Assert.Equal([@"C:\old.mdr"], deleted);
    }

    [Fact]
    public void Split_OnlyTheConfiguredImage_HasNothingDeleted()
    {
        var (reset, deleted) = MainViewModel.SplitCleanedImagePaths(@"C:\new.mdr", new(true, [@"C:\new.mdr"]));

        Assert.Equal([@"C:\new.mdr"], reset);
        Assert.Empty(deleted);
    }

    [Fact]
    public void Split_NothingCleaned_IsEmpty()
    {
        var (reset, deleted) = MainViewModel.SplitCleanedImagePaths(@"C:\new.mdr", new(true, []));

        Assert.Empty(reset);
        Assert.Empty(deleted);
    }

    [Fact]
    public void Split_DiskWithoutImagePath_TreatsEverythingAsDeleted()
    {
        var (reset, deleted) = MainViewModel.SplitCleanedImagePaths(null, new(true, [@"C:\old.mdr"]));

        Assert.Empty(reset);
        Assert.Equal([@"C:\old.mdr"], deleted);
    }

    [Fact]
    public void Describe_NothingCleaned_IsJustTheStatus()
    {
        Assert.Equal("Formatted R:", MainViewModel.DescribeFormatImageCleanup("Formatted R:", null, new(true, [])));
    }

    [Fact]
    public void Describe_ResetOnly_AddsTheResetSentenceOnly()
    {
        var message = MainViewModel.DescribeFormatImageCleanup(
            "Formatted R:", @"C:\new.mdr", new(true, [@"C:\new.mdr"]));

        Assert.Equal($"Formatted R: {Loc.Format("Status.FormatDiskImageReset", @"C:\new.mdr")}", message);
    }

    [Fact]
    public void Describe_ResetAndDeleted_AddsBothSentences_ResetFirst()
    {
        var message = MainViewModel.DescribeFormatImageCleanup(
            "Formatted R:", @"C:\new.mdr", new(true, [@"C:\new.mdr", @"C:\old.mdr"]));

        Assert.Equal(
            $"Formatted R: {Loc.Format("Status.FormatDiskImageReset", @"C:\new.mdr")} " +
            Loc.Format("Status.FormatDiskDeletedImages", @"C:\old.mdr"),
            message);
    }
}
