using ManagedDrive.App.Services;
using ManagedDrive.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="MainViewModel.SelectDisk"/> and the lookup rule behind it. A
/// <see cref="DiskViewModel"/> needs a real mounted disk, so the rule is tested on a stand-in.
/// </summary>
public sealed class MainViewModelSelectDiskTests : IDisposable
{
    /// <summary>
    /// Per-test temporary directory holding the settings file.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    /// <summary>
    /// Removes the temporary directory.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// A disk that is gone reports failure and leaves the selection alone.
    /// </summary>
    [Fact]
    public void SelectDisk_DiskNotMounted_ReturnsFalseAndKeepsSelection()
    {
        using var mountManager = new MountManager();
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        using var viewModel = new MainViewModel(mountManager, store, NullLogger<MainViewModel>.Instance, store.Load().Disks);

        var found = viewModel.SelectDisk("R:");

        Assert.False(found);
        Assert.Null(viewModel.SelectedDisk);
    }

    /// <summary>
    /// The drive letter matches in any case.
    /// </summary>
    [Fact]
    public void FindAvailableDisk_DifferentCase_ReturnsDisk()
    {
        var disk = new StandInDisk("R:", IsRemounting: false);

        var found = Find([new StandInDisk("S:", IsRemounting: false), disk], "r:");

        Assert.Same(disk, found);
    }

    /// <summary>
    /// A disk being remounted by an edit is not available.
    /// </summary>
    [Fact]
    public void FindAvailableDisk_DiskRemounting_ReturnsNull()
    {
        var found = Find([new StandInDisk("R:", IsRemounting: true)], "R:");

        Assert.Null(found);
    }

    /// <summary>
    /// No disk at the mount point gives nothing.
    /// </summary>
    [Fact]
    public void FindAvailableDisk_NoMatch_ReturnsNull()
    {
        var found = Find([new StandInDisk("S:", IsRemounting: false)], "R:");

        Assert.Null(found);
    }

    /// <summary>
    /// Runs the lookup over stand-in disks.
    /// </summary>
    /// <param name="disks">The disks to search.</param>
    /// <param name="mountPoint">The mount point to look up.</param>
    /// <returns>The disk found, or <c>null</c>.</returns>
    private static StandInDisk? Find(StandInDisk[] disks, string mountPoint) =>
        MainViewModel.FindAvailableDisk(disks, mountPoint, d => d.MountPoint, d => d.IsRemounting);

    /// <summary>
    /// Stands in for a <see cref="DiskViewModel"/>.
    /// </summary>
    /// <param name="MountPoint">The disk's mount point.</param>
    /// <param name="IsRemounting">Whether the disk is being remounted.</param>
    private sealed record StandInDisk(string MountPoint, bool IsRemounting);
}
