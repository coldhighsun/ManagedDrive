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

    /// <summary>
    /// Update applies the change to what is on disk and keeps every other field.
    /// </summary>
    [Fact]
    public void Update_AppliesChangeToTheConfigurationOnDisk()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppConfiguration
        {
            Language = "zh-CN",
            Disks = [new DiskProfile { MountPoint = "R:" }],
        });

        store.Update(current => current with { StartMinimized = true });

        var loaded = store.Load();
        Assert.True(loaded.StartMinimized);
        Assert.Equal("zh-CN", loaded.Language);
        Assert.Equal("R:", Assert.Single(loaded.Disks).MountPoint);
    }

    /// <summary>
    /// Two concurrent updates of different fields never lose either change.
    /// </summary>
    [Fact]
    public async Task Update_ConcurrentUpdatesOfDifferentFields_KeepsEveryChange()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppConfiguration());
        var checkedAt = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

        // Each round races two read-modify-writes of different fields; with an unlocked
        // Load-then-Save one of them would regularly overwrite the other with its stale read.
        for (var i = 0; i < 50; i++)
        {
            store.Save(new AppConfiguration());
            using var start = new Barrier(2);

            var first = Task.Run(() =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                store.Update(current => current with { LastUpdateCheckUtc = checkedAt });
            }, TestContext.Current.CancellationToken);
            var second = Task.Run(() =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                store.Update(current => current with { SkippedVersion = "1.2.3" });
            }, TestContext.Current.CancellationToken);
            await Task.WhenAll(first, second);

            var loaded = store.Load();
            Assert.Equal(checkedAt, loaded.LastUpdateCheckUtc);
            Assert.Equal("1.2.3", loaded.SkippedVersion);
        }
    }
}
