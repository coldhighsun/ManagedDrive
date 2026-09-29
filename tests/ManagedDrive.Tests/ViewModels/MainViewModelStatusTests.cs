using ManagedDrive.App.Localization;
using ManagedDrive.App.Services;
using ManagedDrive.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests that problem reports shown via <see cref="MainViewModel.ShowStickyStatus"/> stay in the
/// status bar until an explicit status replaces them.
/// </summary>
public sealed class MainViewModelStatusTests : IDisposable
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
    /// Disk activity right after a failure must not wipe the failure off the status bar.
    /// </summary>
    [Fact]
    public void ShowDiskActivityStatus_WhileStickyStatusIsShown_KeepsTheStickyStatus()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.ShowStickyStatus("Save failed");

        viewModel.ShowDiskActivityStatus("R:", isWrite: true, @"\file.txt");

        Assert.Equal("Save failed", viewModel.StatusText);
    }

    /// <summary>
    /// A routine message (e.g. a later disk's successful auto-mount) doesn't hide a failure.
    /// </summary>
    [Fact]
    public void ShowStatusUnlessSticky_WhileStickyStatusIsShown_KeepsTheStickyStatus()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.ShowStickyStatus("Auto-mount failed");

        viewModel.ShowStatusUnlessSticky("Mounted S:");

        Assert.Equal("Auto-mount failed", viewModel.StatusText);
    }

    /// <summary>
    /// Without a sticky status, routine messages are shown as before.
    /// </summary>
    [Fact]
    public void ShowStatusUnlessSticky_NoStickyStatus_ShowsTheText()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);

        viewModel.ShowStatusUnlessSticky("Mounted S:");

        Assert.Equal("Mounted S:", viewModel.StatusText);
    }

    /// <summary>
    /// An explicit status (the result of something the user did) replaces a sticky one, after
    /// which disk activity is shown again.
    /// </summary>
    [Fact]
    public void StatusText_SetAfterStickyStatus_ReplacesItAndReenablesActivityStatus()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.ShowStickyStatus("Save failed");

        viewModel.StatusText = "Image saved";
        viewModel.ShowDiskActivityStatus("R:", isWrite: true, @"\file.txt");

        Assert.NotEqual("Image saved", viewModel.StatusText);
        Assert.NotEqual("Save failed", viewModel.StatusText);
    }

    /// <summary>
    /// Once the reported problem is resolved, its sticky report is removed and disk activity is
    /// shown again.
    /// </summary>
    [Fact]
    public void ClearStickyStatus_MatchingProblem_RevertsToReadyAndReenablesActivityStatus()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.ShowStickyStatus("R: nearly full", "R:", "IsHighUsage");

        var cleared = viewModel.ClearStickyStatus("R:", "IsHighUsage");
        var afterClear = viewModel.StatusText;
        viewModel.ShowDiskActivityStatus("R:", isWrite: true, @"\file.txt");

        Assert.True(cleared);
        Assert.Equal(Loc.Get("Status.Ready"), afterClear);
        Assert.NotEqual(Loc.Get("Status.Ready"), viewModel.StatusText);
    }

    /// <summary>
    /// Clearing without a problem kind (the disk was removed) removes any report about that disk.
    /// </summary>
    [Fact]
    public void ClearStickyStatus_AnyProblemOfDisk_RevertsToReady()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.ShowStickyStatus("Save failed on R:", "R:", "SaveFailed");

        viewModel.ClearStickyStatus("r:");

        Assert.Equal(Loc.Get("Status.Ready"), viewModel.StatusText);
    }

    /// <summary>
    /// A different problem, a different disk, or a report without a source is left in place.
    /// </summary>
    /// <param name="mountPoint">The disk whose problem is resolved.</param>
    /// <param name="problem">The resolved problem.</param>
    [Theory]
    [InlineData("R:", "IsHighUsage")]
    [InlineData("S:", "SaveFailed")]
    [InlineData("S:", null)]
    public void ClearStickyStatus_OtherProblemOrDisk_KeepsStickyStatus(string mountPoint, string? problem)
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.ShowStickyStatus("Save failed on R:", "R:", "SaveFailed");

        var cleared = viewModel.ClearStickyStatus(mountPoint, problem);

        Assert.False(cleared);
        Assert.Equal("Save failed on R:", viewModel.StatusText);
    }

    /// <summary>
    /// A sticky report without a source disk (e.g. a failed auto-mount) is never cleared this way.
    /// </summary>
    [Fact]
    public void ClearStickyStatus_ReportWithoutSource_KeepsStickyStatus()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.ShowStickyStatus("Auto-mount failed");

        var cleared = viewModel.ClearStickyStatus("R:");

        Assert.False(cleared);
        Assert.Equal("Auto-mount failed", viewModel.StatusText);
    }

    /// <summary>
    /// Once an explicit status replaced the sticky report, clearing the resolved problem must not
    /// overwrite that status.
    /// </summary>
    [Fact]
    public void ClearStickyStatus_AfterExplicitStatus_KeepsExplicitStatus()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.ShowStickyStatus("Save failed on R:", "R:", "SaveFailed");
        viewModel.StatusText = "Image saved";

        var cleared = viewModel.ClearStickyStatus("R:");

        Assert.False(cleared);
        Assert.Equal("Image saved", viewModel.StatusText);
    }

    /// <summary>
    /// Creates a view model with no disks over a settings file in <see cref="_dir"/>.
    /// </summary>
    /// <param name="mountManager">The mount manager the view model uses; owned by the caller.</param>
    /// <returns>The new view model; owned by the caller.</returns>
    private MainViewModel CreateViewModel(MountManager mountManager)
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        return new MainViewModel(mountManager, store, NullLogger<MainViewModel>.Instance, store.Load().Disks);
    }
}
