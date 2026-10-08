using ManagedDrive.App.Localization;
using ManagedDrive.App.Services;
using ManagedDrive.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests the startup auto-mount state behind the tray tooltip's "loading" line, which must stay
/// on until every saved disk has been processed, not just the first.
/// </summary>
public sealed class MainViewModelAutoMountTests : IDisposable
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
    /// Nothing is loading before the auto-mount starts.
    /// </summary>
    [Fact]
    public void IsAutoMounting_Initially_IsFalse()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);

        Assert.False(viewModel.IsAutoMounting);
    }

    /// <summary>
    /// After the first of several disks is done, the others are still loading.
    /// </summary>
    [Fact]
    public void ReportAutoMountDiskDone_OneOfThreeDone_StaysAutoMounting()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.BeginAutoMount(3);

        viewModel.ReportAutoMountDiskDone();

        Assert.True(viewModel.IsAutoMounting);
        Assert.Equal(Loc.Format("Tray.LoadingDisksProgress", 1, 3), viewModel.AutoMountStatusText);
    }

    /// <summary>
    /// Finishing the auto-mount clears the flag, whether or not every disk was reported.
    /// </summary>
    [Fact]
    public void EndAutoMount_AfterBegin_ClearsFlag()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.BeginAutoMount(2);
        viewModel.ReportAutoMountDiskDone();

        viewModel.EndAutoMount();

        Assert.False(viewModel.IsAutoMounting);
    }

    /// <summary>
    /// Zero saved disks never switches the loading state on.
    /// </summary>
    [Fact]
    public void BeginAutoMount_NoDisks_DoesNotStart()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);

        viewModel.BeginAutoMount(0);

        Assert.False(viewModel.IsAutoMounting);
    }

    /// <summary>
    /// Reporting more disks than announced doesn't push the count past the total.
    /// </summary>
    [Fact]
    public void ReportAutoMountDiskDone_MoreThanTotal_CapsAtTotal()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.BeginAutoMount(1);

        viewModel.ReportAutoMountDiskDone();
        viewModel.ReportAutoMountDiskDone();

        Assert.Equal(Loc.Format("Tray.LoadingDisksProgress", 1, 1), viewModel.AutoMountStatusText);
    }

    /// <summary>
    /// Every change of the state is announced so the tooltip refreshes.
    /// </summary>
    [Fact]
    public void BeginAutoMount_Called_RaisesPropertyChangedForFlagAndText()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.BeginAutoMount(2);

        Assert.Contains(nameof(MainViewModel.IsAutoMounting), raised);
        Assert.Contains(nameof(MainViewModel.AutoMountStatusText), raised);
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
