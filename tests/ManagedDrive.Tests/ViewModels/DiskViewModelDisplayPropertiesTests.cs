using System.Reflection;
using ManagedDrive.App.ViewModels;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="DiskViewModel.DisplayPropertyNames"/>, the list that
/// <see cref="DiskViewModel.RefreshDisplay"/> raises when the main window is shown again. A
/// <see cref="DiskViewModel"/> needs a real mounted disk, so the list is checked on its own.
/// </summary>
public sealed class DiskViewModelDisplayPropertiesTests
{
    /// <summary>
    /// Every entry names a public property, so a rename can't silently drop it from the refresh.
    /// </summary>
    [Fact]
    public void DisplayPropertyNames_AllEntries_ArePublicProperties()
    {
        var missing = DiskViewModel.DisplayPropertyNames
            .Where(name => typeof(DiskViewModel).GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is null)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// The list has no duplicates.
    /// </summary>
    [Fact]
    public void DisplayPropertyNames_Entries_AreDistinct()
    {
        Assert.Equal(DiskViewModel.DisplayPropertyNames.Count, DiskViewModel.DisplayPropertyNames.Distinct().Count());
    }

    /// <summary>
    /// The figures an edit from the tray or the CLI changes together are refreshed together, so the
    /// card never shows a new capacity next to stale free space, or a stale label or image path.
    /// </summary>
    [Theory]
    [InlineData(nameof(DiskViewModel.CapacityFormatted))]
    [InlineData(nameof(DiskViewModel.FreeFormatted))]
    [InlineData(nameof(DiskViewModel.FreePercent))]
    [InlineData(nameof(DiskViewModel.UsedFormatted))]
    [InlineData(nameof(DiskViewModel.VolumeLabel))]
    [InlineData(nameof(DiskViewModel.PersistImagePath))]
    [InlineData(nameof(DiskViewModel.ShowLastAutoSave))]
    public void DisplayPropertyNames_EditableFigures_AreIncluded(string propertyName)
    {
        Assert.Contains(propertyName, DiskViewModel.DisplayPropertyNames);
    }

    /// <summary>
    /// The warning icon's visibility is raised with the usage level it derives from, so the card
    /// shown again after the window was hidden never pairs a red bar with a missing icon.
    /// </summary>
    [Theory]
    [InlineData(nameof(DiskViewModel.UsedPercent))]
    [InlineData(nameof(DiskViewModel.UsageLevel))]
    [InlineData(nameof(DiskViewModel.ShowUsageWarning))]
    public void UsagePropertyNames_DerivedUsageFigures_AreIncluded(string propertyName)
    {
        Assert.Contains(propertyName, DiskViewModel.UsagePropertyNames);
    }

    /// <summary>
    /// Every usage entry names a public property.
    /// </summary>
    [Fact]
    public void UsagePropertyNames_AllEntries_ArePublicProperties()
    {
        var missing = DiskViewModel.UsagePropertyNames
            .Where(name => typeof(DiskViewModel).GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is null)
            .ToList();

        Assert.Empty(missing);
    }
}
