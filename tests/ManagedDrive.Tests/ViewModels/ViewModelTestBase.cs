using ManagedDrive.App.Services;
using ManagedDrive.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedDrive.Tests;

/// <summary>
/// Base class for <see cref="MainViewModel"/> tests that need a settings file in a private temporary
/// directory. Owns that directory and everything created through <see cref="CreateViewModel"/>.
/// </summary>
public abstract class ViewModelTestBase : IDisposable
{
    /// <summary>
    /// Things to dispose, in creation order; they are disposed in reverse.
    /// </summary>
    private readonly List<IDisposable> _owned = [];

    /// <summary>
    /// Per-test temporary directory holding the settings file and any image the test writes.
    /// </summary>
    protected string Dir { get; } = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    /// <summary>
    /// Creates a settings store backed by <c>settings.json</c> in <see cref="Dir"/>.
    /// </summary>
    /// <returns>The store.</returns>
    protected SettingsStore CreateStore() => new(Path.Combine(Dir, "settings.json"));

    /// <summary>
    /// Creates a view model over a fresh <see cref="MountManager"/> and the saved profiles of
    /// <paramref name="store"/>, both disposed with the test.
    /// </summary>
    /// <param name="store">The settings store the view model reads and writes.</param>
    /// <returns>The view model.</returns>
    protected MainViewModel CreateViewModel(SettingsStore store)
    {
        var mountManager = new MountManager();
        _owned.Add(mountManager);
        var viewModel = new MainViewModel(mountManager, store, NullLogger<MainViewModel>.Instance, store.Load().Disks);
        _owned.Add(viewModel);
        return viewModel;
    }

    /// <summary>
    /// Disposes what the test created and removes the temporary directory.
    /// </summary>
    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            _owned[i].Dispose();
        }

        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }
}
