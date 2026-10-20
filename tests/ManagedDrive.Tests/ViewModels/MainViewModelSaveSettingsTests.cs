using ManagedDrive.App.Models;
using ManagedDrive.App.ViewModels;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests that saving the disk list does not reset settings owned by other parts of the app.
/// </summary>
public sealed class MainViewModelSaveSettingsTests : ViewModelTestBase
{
    /// <summary>
    /// A declined WinFsp update is stored by the startup check; a later save of the disks must keep it.
    /// </summary>
    [Fact]
    public void SaveSettings_WithSkippedWinFspVersion_KeepsIt()
    {
        var store = CreateStore();
        store.Save(new AppConfiguration { SkippedWinFspVersion = "2.1.0|2.2.26286" });
        var viewModel = CreateViewModel(store);

        viewModel.SaveSettings();

        Assert.Equal("2.1.0|2.2.26286", store.Load().SkippedWinFspVersion);
    }

    /// <summary>
    /// Every setting the view model does not own must come through <c>ApplyLiveSettings</c>
    /// unchanged, while the ones it owns take the live values.
    /// </summary>
    [Fact]
    public void ApplyLiveSettings_OtherSettingsSet_CarriesThemOverAndReplacesOwnedOnes()
    {
        var lastCheck = DateTimeOffset.UtcNow;
        var current = new AppConfiguration
        {
            RunAtStartup = false,
            StartMinimized = true,
            CloseToTray = false,
            Language = "en-US",
            Theme = "light",
            TempDirCompatWarningShown = true,
            ContextMenuEnabled = true,
            AutoCheckForUpdates = false,
            LastUpdateCheckUtc = lastCheck,
            SkippedVersion = "1.2.3",
            SkippedWinFspVersion = "a|b",
            DefaultCompressionLevel = ImageCompressionLevel.Optimal,
            DefaultImageDirectory = @"D:\images",
            SpaceUsageListSize = 7,
        };
        List<DiskProfile> profiles = [new() { MountPoint = "R:" }];

        var result = MainViewModel.ApplyLiveSettings(current, profiles, runAtStartup: true, language: "zh-CN", theme: "dark");

        Assert.True(result.RunAtStartup);
        Assert.Equal("zh-CN", result.Language);
        Assert.Equal("dark", result.Theme);
        Assert.Same(profiles, result.Disks);
        Assert.True(result.StartMinimized);
        Assert.False(result.CloseToTray);
        Assert.True(result.TempDirCompatWarningShown);
        Assert.True(result.ContextMenuEnabled);
        Assert.False(result.AutoCheckForUpdates);
        Assert.Equal(lastCheck, result.LastUpdateCheckUtc);
        Assert.Equal("1.2.3", result.SkippedVersion);
        Assert.Equal("a|b", result.SkippedWinFspVersion);
        Assert.Equal(ImageCompressionLevel.Optimal, result.DefaultCompressionLevel);
        Assert.Equal(@"D:\images", result.DefaultImageDirectory);
        Assert.Equal(7, result.SpaceUsageListSize);
    }
}
