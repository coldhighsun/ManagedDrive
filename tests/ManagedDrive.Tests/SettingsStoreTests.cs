using ManagedDrive.App.Models;
using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    private string SettingsPath => Path.Combine(_dir, "settings.json");

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

    [Fact]
    public void SaveThenLoad_RoundTripsConfigurationAndLeavesNoTempFile()
    {
        var store = new SettingsStore(SettingsPath);
        var config = new AppConfiguration
        {
            Language = "zh-CN",
            Disks = [new DiskProfile { MountPoint = "R:", VolumeLabel = "Scratch" }],
        };

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal("zh-CN", loaded.Language);
        var disk = Assert.Single(loaded.Disks);
        Assert.Equal("R:", disk.MountPoint);
        Assert.Equal("Scratch", disk.VolumeLabel);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Save_OverExistingFile_ReplacesIt()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppConfiguration { Language = "en-US" });

        store.Save(new AppConfiguration { Language = "zh-CN" });

        Assert.Equal("zh-CN", store.Load().Language);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new SettingsStore(SettingsPath);

        var loaded = store.Load();

        Assert.Empty(loaded.Disks);
        Assert.Null(loaded.Language);
    }

    [Fact]
    public void Load_TruncatedFile_ReturnsDefaultsAndPreservesTheOriginalCopy()
    {
        var store = new SettingsStore(SettingsPath);
        const string truncated = "{ \"Disks\": [ { \"MountPoint\": \"R:\"";
        File.WriteAllText(SettingsPath, truncated);

        var loaded = store.Load();

        Assert.Empty(loaded.Disks);
        var backup = Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
        Assert.Equal(truncated, File.ReadAllText(backup));
    }
}
