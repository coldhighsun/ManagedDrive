using ManagedDrive.App.Models;
using ManagedDrive.App.Views;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for how <see cref="SettingsDialog"/>'s confirmed edits are merged into the saved
/// configuration.
/// </summary>
public sealed class SettingsDialogTests
{
    /// <summary>
    /// Fields the dialog doesn't edit keep the values written while it was open; the fields it
    /// does edit take the dialog's values.
    /// </summary>
    [Fact]
    public void ApplyEdits_LatestChangedWhileDialogWasOpen_KeepsLatestNonEditedFieldsAndTakesEdits()
    {
        var openedAt = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
        var checkedAt = openedAt.AddDays(1);
        var latest = new AppConfiguration
        {
            Disks = [new DiskProfile { MountPoint = "R:" }, new DiskProfile { MountPoint = "S:" }],
            TempDirCompatWarningShown = true,
            LastUpdateCheckUtc = checkedAt,
            SkippedVersion = "2.0.0",
        };
        var edits = new AppConfiguration
        {
            Disks = [new DiskProfile { MountPoint = "R:" }],
            LastUpdateCheckUtc = openedAt,
            RunAtStartup = true,
            StartMinimized = true,
            CloseToTray = false,
            Language = "zh-CN",
            Theme = "Dark",
            ContextMenuEnabled = true,
            AutoCheckForUpdates = false,
            DefaultCompressionLevel = ImageCompressionLevel.SmallestSize,
            DefaultImageDirectory = @"D:\Images",
        };

        var result = SettingsDialog.ApplyEdits(latest, edits);

        Assert.Equal(["R:", "S:"], result.Disks.Select(d => d.MountPoint));
        Assert.True(result.TempDirCompatWarningShown);
        Assert.Equal(checkedAt, result.LastUpdateCheckUtc);
        Assert.Equal("2.0.0", result.SkippedVersion);
        Assert.True(result.RunAtStartup);
        Assert.True(result.StartMinimized);
        Assert.False(result.CloseToTray);
        Assert.Equal("zh-CN", result.Language);
        Assert.Equal("Dark", result.Theme);
        Assert.True(result.ContextMenuEnabled);
        Assert.False(result.AutoCheckForUpdates);
        Assert.Equal(ImageCompressionLevel.SmallestSize, result.DefaultCompressionLevel);
        Assert.Equal(@"D:\Images", result.DefaultImageDirectory);
    }
}
