using ManagedDrive.App.Models;
using ManagedDrive.App.Services;
using ManagedDrive.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for how <see cref="MainViewModel.SaveSettings"/> treats saved profiles that are not
/// mounted in the current session.
/// </summary>
public sealed class MainViewModelSettingsTests : IDisposable
{
    /// <summary>
    /// Per-test temporary directory holding the settings file.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    /// <summary>
    /// Path of the settings file used by the test's <see cref="SettingsStore"/>.
    /// </summary>
    private string SettingsPath => Path.Combine(_dir, "settings.json");

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
    /// A save before the auto-mount loop reaches a profile, and a profile with auto-mount off,
    /// must not delete either from the settings.
    /// </summary>
    [Fact]
    public void SaveSettings_BeforeAnySavedProfileIsMounted_KeepsAllProfiles()
    {
        var store = new SettingsStore(SettingsPath);
        var pending = new DiskProfile { MountPoint = "R:", AutoMount = true, PersistImagePath = @"C:\images\r.mdr" };
        var manual = new DiskProfile { MountPoint = "S:", AutoMount = false, PersistImagePath = @"C:\images\s.mdr" };
        store.Save(new AppConfiguration { Disks = [pending, manual] });

        using var mountManager = new MountManager();
        using var viewModel = new MainViewModel(mountManager, store, NullLogger<MainViewModel>.Instance, store.Load().Disks);

        viewModel.SaveSettings();

        var saved = store.Load().Disks;
        Assert.Equal(2, saved.Count);
        Assert.Contains(pending, saved);
        Assert.Contains(manual, saved);
    }

    /// <summary>
    /// A profile that fails to mount (here: a corrupt image, which fails before any volume is
    /// mounted) stays saved, once, however many times the mount is retried.
    /// </summary>
    [Fact]
    public async Task MountFromProfileAsync_FailsRepeatedly_ProfileIsSavedOnce()
    {
        Directory.CreateDirectory(_dir);
        var imagePath = Path.Combine(_dir, "corrupt.mdr");
        await File.WriteAllBytesAsync(imagePath, [0xDE, 0xAD, 0xBE, 0xEF], TestContext.Current.CancellationToken);

        // A directory mount point, so the failure path's TEMP reset can't match the real user TEMP.
        var profile = new DiskProfile
        {
            MountPoint = Path.Combine(_dir, "mnt"),
            AutoMount = true,
            CapacityBytes = 64UL * 1024 * 1024,
            PersistImagePath = imagePath,
        };
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppConfiguration { Disks = [profile] });

        using var mountManager = new MountManager();
        using var viewModel = new MainViewModel(mountManager, store, NullLogger<MainViewModel>.Instance, store.Load().Disks);

        var first = await viewModel.MountFromProfileAsync(profile);
        var second = await viewModel.MountFromProfileAsync(profile);
        viewModel.SaveSettings();

        Assert.False(first);
        Assert.False(second);
        Assert.Equal([profile], store.Load().Disks);
    }

    /// <summary>
    /// A manual profile with no image and no source archive is never mounted and has no image
    /// options worth remembering, so it is dropped rather than retained across the session.
    /// </summary>
    [Fact]
    public void SaveSettings_ManualProfileWithoutBackingFile_IsNotRetained()
    {
        var store = new SettingsStore(SettingsPath);
        var unbacked = new DiskProfile { MountPoint = "S:", AutoMount = false };
        store.Save(new AppConfiguration { Disks = [unbacked] });

        using var mountManager = new MountManager();
        using var viewModel = new MainViewModel(mountManager, store, NullLogger<MainViewModel>.Instance, store.Load().Disks);

        viewModel.SaveSettings();

        Assert.Empty(store.Load().Disks);
    }
}
