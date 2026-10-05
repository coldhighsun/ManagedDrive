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

    /// <summary>
    /// A lock that is released while Update is still retrying doesn't cost the update: it is
    /// applied on a later attempt.
    /// </summary>
    [Fact]
    public async Task Update_FileLockReleasedDuringRetries_AppliesTheUpdate()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppConfiguration { Language = "zh-CN" });

        var lockHolder = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken);
            lockHolder.Dispose();
        }, TestContext.Current.CancellationToken);

        store.Update(current => current with { SkippedVersion = "1.2.3" });
        await release;

        var loaded = store.Load();
        Assert.Equal("1.2.3", loaded.SkippedVersion);
        Assert.Equal("zh-CN", loaded.Language);
    }

    /// <summary>
    /// A lock that is released while Load is still retrying doesn't make Load return defaults.
    /// </summary>
    [Fact]
    public async Task Load_FileLockReleasedDuringRetries_ReturnsSavedSettings()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppConfiguration { Language = "zh-CN" });

        var lockHolder = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken);
            lockHolder.Dispose();
        }, TestContext.Current.CancellationToken);

        var loaded = store.Load();
        await release;

        Assert.Equal("zh-CN", loaded.Language);
    }

    /// <summary>
    /// A Load that fails later in the session (after a successful startup Load) must not disable
    /// Update: its defaults are not what the app's in-memory state was built from.
    /// </summary>
    [Fact]
    public void Update_AfterLaterLoadFailedOnLockedFile_StillSaves()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppConfiguration { Language = "zh-CN" });
        store.Load();

        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Load();
        }

        store.Update(current => current with { SkippedVersion = "1.2.3" });

        Assert.Equal("1.2.3", store.Load().SkippedVersion);
    }

    /// <summary>
    /// After Load gave up on a locked file, Update must not write state derived from the returned
    /// defaults over the real settings, even once the lock is gone.
    /// </summary>
    [Fact]
    public void Update_AfterLoadFailedOnLockedFile_DoesNotOverwriteSettings()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppConfiguration { Language = "zh-CN" });

        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Load();
        }

        store.Update(current => current with { SkippedVersion = "1.2.3" });

        var loaded = store.Load();
        Assert.Equal("zh-CN", loaded.Language);
        Assert.Null(loaded.SkippedVersion);
    }

    /// <summary>
    /// A settings file that stays locked leaves the existing settings untouched and creates no
    /// <c>.corrupt</c> copy of the healthy file.
    /// </summary>
    [Fact]
    public void Update_FileStaysLocked_LeavesNoCorruptCopy()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppConfiguration { Language = "zh-CN" });

        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Update(current => current with { SkippedVersion = "1.2.3" });
        }

        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt*"));
        Assert.Equal("zh-CN", store.Load().Language);
    }

    /// <summary>
    /// A settings file whose list or entries are JSON null loads with a usable disk list.
    /// </summary>
    [Fact]
    public void Load_NullDisksOrEntries_DropsInvalidEntries()
    {
        var store = new SettingsStore(SettingsPath);
        File.WriteAllText(SettingsPath, "{ \"Disks\": [ null, { \"MountPoint\": null }, { \"MountPoint\": \"R:\" } ] }");

        var loaded = store.Load();

        Assert.Equal("R:", Assert.Single(loaded.Disks).MountPoint);

        File.WriteAllText(SettingsPath, "{ \"Disks\": null }");
        Assert.Empty(store.Load().Disks);
    }

    /// <summary>
    /// A failed write (here: the temp file is held open) is logged, not thrown, so a caller's
    /// already-successful operation isn't reported as failed.
    /// </summary>
    [Fact]
    public void Update_SaveFailsWithIoError_DoesNotThrow()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppConfiguration { Language = "zh-CN" });

        using (new FileStream(SettingsPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var saved = true;
            var exception = Record.Exception(() => saved = store.Update(current => current with { SkippedVersion = "1.2.3" }));

            Assert.Null(exception);
            Assert.False(saved);
        }

        Assert.Equal("zh-CN", store.Load().Language);
    }

    /// <summary>
    /// A successful update reports that it was written.
    /// </summary>
    [Fact]
    public void Update_Succeeds_ReturnsTrue()
    {
        var store = new SettingsStore(SettingsPath);

        var saved = store.Update(current => current with { SkippedVersion = "1.2.3" });

        Assert.True(saved);
    }

    /// <summary>
    /// Entries dropped as invalid are kept in a <c>.corrupt</c> copy of the original file, since the
    /// next save replaces it.
    /// </summary>
    [Fact]
    public void Load_InvalidDiskEntries_PreservesTheOriginalFile()
    {
        var store = new SettingsStore(SettingsPath);
        const string original = "{ \"Disks\": [ { \"MountPoint\": \"\" }, { \"MountPoint\": \"R:\" } ] }";
        File.WriteAllText(SettingsPath, original);

        store.Load();

        var backup = Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
        Assert.Equal(original, File.ReadAllText(backup));
    }

    /// <summary>
    /// Reading the same invalid file repeatedly keeps a single backup, while a different invalid
    /// file gets its own.
    /// </summary>
    [Fact]
    public void Load_SameInvalidFileRepeatedly_KeepsOneBackup()
    {
        var store = new SettingsStore(SettingsPath);
        File.WriteAllText(SettingsPath, "{ \"Disks\": [ { \"MountPoint\": \"\" } ] }");

        store.Load();
        store.Load();
        store.Update(current => current);

        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));

        File.WriteAllText(SettingsPath, "{ \"Disks\": [ { \"MountPoint\": \"\", \"VolumeLabel\": \"x\" } ] }");
        store.Load();

        Assert.Equal(2, Directory.GetFiles(_dir, "settings.json.corrupt-*").Length);
    }

    /// <summary>
    /// A settings file locked for the whole call is not overwritten with defaults.
    /// </summary>
    [Fact]
    public void Update_FileLockedByAnotherProcess_DoesNotOverwriteSettingsWithDefaults()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppConfiguration { Language = "zh-CN" });

        // Simulates an antivirus or backup tool briefly holding the file exclusively.
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Update(current => current with { SkippedVersion = "1.2.3" });
        }

        var loaded = store.Load();
        Assert.Equal("zh-CN", loaded.Language);
    }
}
