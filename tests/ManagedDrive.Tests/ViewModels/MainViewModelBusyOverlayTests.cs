using ManagedDrive.App.Services;
using ManagedDrive.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests that the <see cref="MainViewModel"/> commands which run under the busy overlay are
/// unavailable while another operation is showing it.
/// </summary>
public sealed class MainViewModelBusyOverlayTests : IDisposable
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
    /// Import commands can't be started while the overlay shows another operation.
    /// </summary>
    [Fact]
    public void ImportCommands_CanExecuteWhileBusyOverlayIsShown_ReturnsFalse()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);

        viewModel.BusyOverlay.Start("Working...");

        Assert.False(viewModel.ImportDiskCommand.CanExecute(null));
        Assert.False(viewModel.ImportArchiveCommand.CanExecute(null));
    }

    /// <summary>
    /// Import commands become available again once the running operation stops the overlay.
    /// </summary>
    [Fact]
    public void ImportCommands_CanExecuteAfterBusyOverlayStops_ReturnsTrue()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.BusyOverlay.Start("Working...");

        viewModel.BusyOverlay.Stop();

        Assert.True(viewModel.ImportDiskCommand.CanExecute(null));
        Assert.True(viewModel.ImportArchiveCommand.CanExecute(null));
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
