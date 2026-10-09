using ManagedDrive.App.Views;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="CreateDiskDialog"/>'s WPF-free helpers.
/// </summary>
public sealed class CreateDiskDialogTests
{
    /// <summary>
    /// The slider range grows to cover an edited disk's existing value, never shrinks below the
    /// range offered for new values, and never overflows <see cref="int"/>.
    /// </summary>
    /// <param name="defaultMaximum">The maximum offered for new values.</param>
    /// <param name="existingValue">The edited disk's current value.</param>
    /// <param name="expected">The expected slider maximum.</param>
    [Theory]
    [InlineData(60, 10UL, 60)]
    [InlineData(60, 60UL, 60)]
    [InlineData(60, 1440UL, 1440)]
    [InlineData(20, (ulong)uint.MaxValue, int.MaxValue)]
    public void CoverExistingValue_ExistingValue_ReturnsMaximumCoveringIt(
        int defaultMaximum, ulong existingValue, int expected)
    {
        var maximum = CreateDiskDialog.CoverExistingValue(defaultMaximum, existingValue);

        Assert.Equal(expected, maximum);
    }

    /// <summary>
    /// Values above the normal range snap to the nearer of the normal maximum and the edited
    /// disk's existing value, since the builder rejects anything in between; values within the
    /// normal range, or without an extension, are left alone.
    /// </summary>
    /// <param name="value">The slider value.</param>
    /// <param name="existingValue">The edited disk's existing value, or <c>0</c> for none.</param>
    /// <param name="expected">The expected snapped value.</param>
    [Theory]
    [InlineData(30, 1440UL, 30)]
    [InlineData(60, 1440UL, 60)]
    [InlineData(61, 1440UL, 60)]
    [InlineData(749, 1440UL, 60)]
    [InlineData(751, 1440UL, 1440)]
    [InlineData(1440, 1440UL, 1440)]
    [InlineData(90, 0UL, 90)]
    [InlineData(90, 40UL, 90)]
    public void SnapAboveNormalRange_Value_SnapsToAnAcceptedValue(int value, ulong existingValue, int expected)
    {
        ulong? existing = existingValue == 0 ? null : existingValue;

        var snapped = CreateDiskDialog.SnapAboveNormalRange(value, normalMaximum: 60, existing);

        Assert.Equal(expected, snapped);
    }

    /// <summary>
    /// Ticking read-only is refused only for a user action after loading while a preset is ticked
    /// or TEMP/TMP is redirected (also by a redirect no preset owns).
    /// </summary>
    /// <param name="isChecked">Whether the box is now checked.</param>
    /// <param name="isLoaded">Whether the dialog has loaded.</param>
    /// <param name="anyPresetTicked">Whether a preset is ticked.</param>
    /// <param name="redirectsTemp">Whether TEMP/TMP is redirected.</param>
    /// <param name="expected">Whether the tick is refused.</param>
    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, true, true, true, false)]
    public void ShouldRefuseReadOnly_Combinations_RefusesOnlyUserTickWithPresetsOrTemp(
        bool isChecked, bool isLoaded, bool anyPresetTicked, bool redirectsTemp, bool expected)
    {
        Assert.Equal(expected, CreateDiskDialog.ShouldRefuseReadOnly(isChecked, isLoaded, anyPresetTicked, redirectsTemp));
    }

    /// <summary>
    /// Saving as read-only drops whatever folders or redirections the disk has, preset-owned or hand-set.
    /// </summary>
    [Fact]
    public void RemovesEntriesOnSave_FoldersOrRedirects_ReturnsTrue()
    {
        var redirects = new List<EnvRedirect> { new() { Variable = "npm_config_cache", SubPath = "x" } };

        Assert.True(CreateDiskDialog.RemovesEntriesOnSave(["cache"], [], false));
        Assert.True(CreateDiskDialog.RemovesEntriesOnSave([], redirects, false));
        Assert.True(CreateDiskDialog.RemovesEntriesOnSave([], [], true));
    }

    /// <summary>
    /// A disk with nothing to drop needs no warning.
    /// </summary>
    [Fact]
    public void RemovesEntriesOnSave_Nothing_ReturnsFalse()
    {
        Assert.False(CreateDiskDialog.RemovesEntriesOnSave([], [], false));
    }
}
