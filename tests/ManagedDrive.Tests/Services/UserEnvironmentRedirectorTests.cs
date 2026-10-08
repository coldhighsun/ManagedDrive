using ManagedDrive.App.Models;
using ManagedDrive.App.Services;
using Microsoft.Win32;

namespace ManagedDrive.Tests;

public sealed class UserEnvironmentRedirectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());
    private readonly FakeUserEnvironment _env = new();
    private List<EnvRedirectBackup> _backups = [];
    private bool _saveSucceeds = true;

    private UserEnvironmentRedirector CreateRedirector() => new(
        _env,
        () => _backups,
        list =>
        {
            if (_saveSucceeds)
            {
                _backups = list;
            }

            return _saveSucceeds;
        });

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static DiskOptions DiskAt(string mountPoint, string variable, string subPath) => new()
    {
        MountPoint = mountPoint,
        CapacityBytes = 1024 * 1024,
        EnvRedirects = [new() { Variable = variable, SubPath = subPath }],
    };

    [Fact]
    public void ApplyDiskEffects_CreatesFoldersAndPointsVariablesIntoTheDisk()
    {
        var mount = Path.Combine(_root, "disk");
        Directory.CreateDirectory(mount);
        var options = DiskAt(mount, "MY_CACHE", "cache") with { Folders = [@"a\b"] };

        var result = CreateRedirector().ApplyDiskEffects(options);

        Assert.True(result.IsComplete);
        Assert.Equal(["MY_CACHE"], result.Applied);
        Assert.True(Directory.Exists(Path.Combine(mount, "cache")));
        Assert.True(Directory.Exists(Path.Combine(mount, "a", "b")));
        Assert.Equal(Path.Combine(mount, "cache"), _env.Values["MY_CACHE"].Text);
        Assert.Equal(1, _env.Broadcasts);
    }

    [Fact]
    public void ApplyDiskEffects_RecordsTheOriginalValueAndKind()
    {
        _env.Values["MY_CACHE"] = new(@"%USERPROFILE%\old", RegistryValueKind.ExpandString);
        var mount = Path.Combine(_root, "disk");

        CreateRedirector().ApplyDiskEffects(DiskAt(mount, "MY_CACHE", "c"));

        var backup = Assert.Single(_backups);
        Assert.Equal(@"%USERPROFILE%\old", backup.OriginalText);
        Assert.Equal(RegistryValueKind.ExpandString, backup.OriginalKind);
        Assert.Equal(mount, backup.MountPoint);
    }

    [Fact]
    public void ApplyDiskEffects_Twice_KeepsTheFirstOriginalValue()
    {
        _env.Values["MY_CACHE"] = new("original", RegistryValueKind.String);
        var redirector = CreateRedirector();
        var options = DiskAt(Path.Combine(_root, "disk"), "MY_CACHE", "c");

        redirector.ApplyDiskEffects(options);
        redirector.ApplyDiskEffects(options);

        var backup = Assert.Single(_backups);
        Assert.Equal("original", backup.OriginalText);
    }

    [Fact]
    public void ApplyDiskEffects_VariableOwnedByAnotherDisk_IsRejectedAndLeftAlone()
    {
        var redirector = CreateRedirector();
        var first = Path.Combine(_root, "one");
        var second = Path.Combine(_root, "two");
        redirector.ApplyDiskEffects(DiskAt(first, "MY_CACHE", "c"));

        var result = redirector.ApplyDiskEffects(DiskAt(second, "MY_CACHE", "c"));

        Assert.False(result.IsComplete);
        Assert.Equal(["MY_CACHE"], result.Rejected);
        Assert.Equal(Path.Combine(first, "c"), _env.Values["MY_CACHE"].Text);
    }

    [Fact]
    public void ApplyDiskEffects_ReservedVariable_IsNotWritten()
    {
        var result = CreateRedirector().ApplyDiskEffects(DiskAt(Path.Combine(_root, "d"), "PATH", "c"));

        Assert.Equal(["PATH"], result.Failed);
        Assert.False(_env.Values.ContainsKey("PATH"));
        Assert.Empty(_backups);
    }

    [Fact]
    public void ApplyDiskEffects_BackupCannotBeSaved_DoesNotTouchTheVariable()
    {
        _saveSucceeds = false;

        var result = CreateRedirector().ApplyDiskEffects(DiskAt(Path.Combine(_root, "d"), "MY_CACHE", "c"));

        Assert.Equal(["MY_CACHE"], result.Failed);
        Assert.False(_env.Values.ContainsKey("MY_CACHE"));
        Assert.Equal(0, _env.Broadcasts);
    }

    [Fact]
    public void ApplyDiskEffects_DiskNoLongerCurrent_WritesNothing()
    {
        var result = CreateRedirector().ApplyDiskEffects(
            DiskAt(Path.Combine(_root, "d"), "MY_CACHE", "c"), isCurrent: () => false);

        Assert.Empty(result.Applied);
        Assert.False(_env.Values.ContainsKey("MY_CACHE"));
        Assert.Empty(_backups);
        Assert.Equal(0, _env.Broadcasts);
    }

    [Fact]
    public void ApplyDiskEffects_DiskStopsBeingCurrentMidway_KeepsWhatWasDoneAndSkipsTheRest()
    {
        var calls = 0;
        var options = new DiskOptions
        {
            MountPoint = Path.Combine(_root, "d"),
            CapacityBytes = 1024 * 1024,
            EnvRedirects = [new() { Variable = "FIRST_CACHE", SubPath = "a" }, new() { Variable = "SECOND_CACHE", SubPath = "b" }],
        };

        var result = CreateRedirector().ApplyDiskEffects(options, isCurrent: () => ++calls == 1);

        Assert.Equal(["FIRST_CACHE"], result.Applied);
        Assert.False(_env.Values.ContainsKey("SECOND_CACHE"));
        Assert.Equal("FIRST_CACHE", Assert.Single(_backups).Variable);
    }

    [Fact]
    public void ApplyDiskEffects_BackupSaveFailsForOneVariable_IsNotPersistedWithTheNext()
    {
        var saves = 0;
        var redirector = new UserEnvironmentRedirector(
            _env,
            () => _backups,
            list =>
            {
                // Only the first save fails.
                if (saves++ == 0)
                {
                    return false;
                }

                _backups = list;
                return true;
            });
        var options = new DiskOptions
        {
            MountPoint = Path.Combine(_root, "d"),
            CapacityBytes = 1024 * 1024,
            EnvRedirects = [new() { Variable = "FIRST_CACHE", SubPath = "a" }, new() { Variable = "SECOND_CACHE", SubPath = "b" }],
        };

        var result = redirector.ApplyDiskEffects(options);

        Assert.Equal(["FIRST_CACHE"], result.Failed);
        Assert.Equal(["SECOND_CACHE"], result.Applied);
        Assert.Equal("SECOND_CACHE", Assert.Single(_backups).Variable);
        Assert.False(_env.Values.ContainsKey("FIRST_CACHE"));
    }

    [Fact]
    public void ApplyDiskEffects_NothingToDo_DoesNotBroadcast()
    {
        var result = CreateRedirector().ApplyDiskEffects(new() { MountPoint = _root, CapacityBytes = 1024 * 1024 });

        Assert.True(result.IsComplete);
        Assert.Equal(0, _env.Broadcasts);
    }

    [Fact]
    public void Restore_PutsBackTheOriginalValueWithItsKind()
    {
        _env.Values["MY_CACHE"] = new(@"%USERPROFILE%\old", RegistryValueKind.ExpandString);
        var redirector = CreateRedirector();
        var mount = Path.Combine(_root, "disk");
        redirector.ApplyDiskEffects(DiskAt(mount, "MY_CACHE", "c"));

        var restored = redirector.Restore(mount);

        Assert.Equal(1, restored);
        Assert.Equal(new(@"%USERPROFILE%\old", RegistryValueKind.ExpandString), _env.Values["MY_CACHE"]);
        Assert.Empty(_backups);
    }

    [Fact]
    public void Restore_VariableWasNotSetBefore_DeletesIt()
    {
        var redirector = CreateRedirector();
        var mount = Path.Combine(_root, "disk");
        redirector.ApplyDiskEffects(DiskAt(mount, "MY_CACHE", "c"));

        redirector.Restore(mount);

        Assert.False(_env.Values.ContainsKey("MY_CACHE"));
    }

    [Fact]
    public void Restore_ValueChangedByTheUserMeanwhile_IsLeftAloneButBackupDropped()
    {
        var redirector = CreateRedirector();
        var mount = Path.Combine(_root, "disk");
        redirector.ApplyDiskEffects(DiskAt(mount, "MY_CACHE", "c"));
        _env.Values["MY_CACHE"] = new(@"D:\mine", RegistryValueKind.String);

        var restored = redirector.Restore(mount);

        Assert.Equal(0, restored);
        Assert.Equal(@"D:\mine", _env.Values["MY_CACHE"].Text);
        Assert.Empty(_backups);
    }

    [Fact]
    public void Restore_OnlyTouchesTheGivenDisksVariables()
    {
        var redirector = CreateRedirector();
        var one = Path.Combine(_root, "one");
        var two = Path.Combine(_root, "two");
        redirector.ApplyDiskEffects(DiskAt(one, "ONE_CACHE", "c"));
        redirector.ApplyDiskEffects(DiskAt(two, "TWO_CACHE", "c"));

        redirector.Restore(one);

        Assert.False(_env.Values.ContainsKey("ONE_CACHE"));
        Assert.True(_env.Values.ContainsKey("TWO_CACHE"));
        Assert.Equal("TWO_CACHE", Assert.Single(_backups).Variable);
    }

    [Fact]
    public void RestoreVariables_OnlyTouchesTheNamedVariablesOfAnyDisk()
    {
        _env.Values["TEMP"] = new(@"%USERPROFILE%\AppData\Local\Temp", RegistryValueKind.ExpandString);
        _env.Values["TMP"] = new(@"%USERPROFILE%\AppData\Local\Temp", RegistryValueKind.ExpandString);
        var redirector = CreateRedirector();
        var temp = new DiskOptions
        {
            MountPoint = Path.Combine(_root, "t"),
            CapacityBytes = 1024 * 1024,
            EnvRedirects = [new() { Variable = "TEMP", SubPath = "Temp" }, new() { Variable = "tmp", SubPath = "Temp" }],
        };
        redirector.ApplyDiskEffects(temp);
        redirector.ApplyDiskEffects(DiskAt(Path.Combine(_root, "c"), "MY_CACHE", "cache"));

        var restored = redirector.RestoreVariables(["TEMP", "TMP"]);

        Assert.Equal(2, restored);
        Assert.Equal(new(@"%USERPROFILE%\AppData\Local\Temp", RegistryValueKind.ExpandString), _env.Values["TEMP"]);
        Assert.Equal(RegistryValueKind.ExpandString, _env.Values["tmp"].Kind);
        Assert.Equal(Path.Combine(_root, "c", "cache"), _env.Values["MY_CACHE"].Text);
        Assert.Equal(["MY_CACHE"], _backups.Select(b => b.Variable));
    }

    [Fact]
    public void RestoreVariables_WithMountPoint_LeavesTheSameVariableOfAnotherDisk()
    {
        var redirector = CreateRedirector();
        var first = Path.Combine(_root, "a");
        var second = Path.Combine(_root, "b");
        redirector.ApplyDiskEffects(DiskAt(first, "MY_CACHE", "c"));
        redirector.ApplyDiskEffects(DiskAt(second, "OTHER_CACHE", "c"));

        var restored = redirector.RestoreVariables(["MY_CACHE", "OTHER_CACHE"], mountPoint: first);

        Assert.Equal(1, restored);
        Assert.False(_env.Values.ContainsKey("MY_CACHE"));
        Assert.Equal(Path.Combine(second, "c"), _env.Values["OTHER_CACHE"].Text);
        Assert.Equal(["OTHER_CACHE"], _backups.Select(b => b.Variable));
    }

    [Fact]
    public void PointsInto_VariableSetToTheDisksFolder_IsTrueWhoeverSetIt()
    {
        var mount = Path.Combine(_root, "d");
        _env.Values["MY_CACHE"] = new(Path.Combine(mount, "cache") + @"\", RegistryValueKind.String);

        Assert.True(CreateRedirector().PointsInto(mount, new() { Variable = "my_cache", SubPath = "cache" }));
    }

    [Fact]
    public void PointsInto_UnsetOtherFolderOrOtherDisk_IsFalse()
    {
        var mount = Path.Combine(_root, "d");
        _env.Values["MY_CACHE"] = new(Path.Combine(_root, "other", "cache"), RegistryValueKind.String);
        var redirector = CreateRedirector();

        Assert.False(redirector.PointsInto(mount, new() { Variable = "MY_CACHE", SubPath = "cache" }));
        Assert.False(redirector.PointsInto(mount, new() { Variable = "NOT_SET", SubPath = "cache" }));
        Assert.False(redirector.PointsInto(mount, new() { Variable = "MY_CACHE", SubPath = ".." }));
    }

    [Fact]
    public void RestoreVariables_NothingRecorded_RestoresNothing()
    {
        _env.Values["TEMP"] = new(@"R:\Temp", RegistryValueKind.String);

        var restored = CreateRedirector().RestoreVariables(["TEMP", "TMP"]);

        Assert.Equal(0, restored);
        Assert.Equal(@"R:\Temp", _env.Values["TEMP"].Text);
        Assert.Equal(0, _env.Broadcasts);
    }

    [Fact]
    public void Restore_UnknownMountPoint_ChangesNothing()
    {
        var redirector = CreateRedirector();

        Assert.Equal(0, redirector.Restore(Path.Combine(_root, "none")));
        Assert.Equal(0, _env.Broadcasts);
    }

    [Fact]
    public void Restore_MountPointComparisonIgnoresCase()
    {
        var redirector = CreateRedirector();
        var mount = Path.Combine(_root, "Disk");
        redirector.ApplyDiskEffects(DiskAt(mount, "MY_CACHE", "c"));

        Assert.Equal(1, redirector.Restore(mount.ToUpperInvariant()));
    }

    [Fact]
    public void RestoreAll_RestoresEveryDisksVariablesAndForgetsTheBackups()
    {
        _env.Values["ONE_CACHE"] = new("old", RegistryValueKind.String);
        var redirector = CreateRedirector();
        redirector.ApplyDiskEffects(DiskAt(Path.Combine(_root, "one"), "ONE_CACHE", "c"));
        redirector.ApplyDiskEffects(DiskAt(Path.Combine(_root, "two"), "TWO_CACHE", "c"));

        var restored = redirector.RestoreAll();

        Assert.Equal(2, restored);
        Assert.Equal("old", _env.Values["ONE_CACHE"].Text);
        Assert.False(_env.Values.ContainsKey("TWO_CACHE"));
        Assert.Empty(_backups);
    }

    [Fact]
    public void RestoreAll_WithoutBroadcast_RestoresButDoesNotAnnounce()
    {
        var redirector = CreateRedirector();
        redirector.ApplyDiskEffects(DiskAt(Path.Combine(_root, "one"), "ONE_CACHE", "c"));
        var broadcastsBefore = _env.Broadcasts;

        var restored = redirector.RestoreAll(broadcast: false);

        Assert.Equal(1, restored);
        Assert.False(_env.Values.ContainsKey("ONE_CACHE"));
        Assert.Equal(broadcastsBefore, _env.Broadcasts);
    }

    [Fact]
    public void RestoreAll_CalledTwice_SecondCallIsANoOp()
    {
        _env.Values["ONE_CACHE"] = new("old", RegistryValueKind.String);
        var redirector = CreateRedirector();
        redirector.ApplyDiskEffects(DiskAt(Path.Combine(_root, "one"), "ONE_CACHE", "c"));
        redirector.RestoreAll();
        var broadcastsBefore = _env.Broadcasts;

        var restored = redirector.RestoreAll();

        Assert.Equal(0, restored);
        Assert.Equal("old", _env.Values["ONE_CACHE"].Text);
        Assert.Equal(broadcastsBefore, _env.Broadcasts);
    }

    [Fact]
    public void RestoreDangling_RestoresOnlyDisksThatAreNotMounted()
    {
        var redirector = CreateRedirector();
        var live = Path.Combine(_root, "live");
        redirector.ApplyDiskEffects(DiskAt(live, "LIVE_CACHE", "c"));
        redirector.ApplyDiskEffects(DiskAt(Path.Combine(_root, "gone"), "GONE_CACHE", "c"));

        var restored = redirector.RestoreDangling(mountPoint => mountPoint == live);

        Assert.Equal(1, restored);
        Assert.True(_env.Values.ContainsKey("LIVE_CACHE"));
        Assert.False(_env.Values.ContainsKey("GONE_CACHE"));
    }

    [Fact]
    public void RestoreDangling_AfterACrash_ReappliedDiskKeepsTheOriginalValue()
    {
        _env.Values["MY_CACHE"] = new("original", RegistryValueKind.String);
        var mount = Path.Combine(_root, "disk");
        var options = DiskAt(mount, "MY_CACHE", "c");
        CreateRedirector().ApplyDiskEffects(options);

        // The app died: a new redirector sees the leftover backup and the same disk mounts again.
        var afterRestart = CreateRedirector();
        afterRestart.ApplyDiskEffects(options);
        afterRestart.Restore(mount);

        Assert.Equal("original", _env.Values["MY_CACHE"].Text);
    }

    /// <summary>
    /// In-memory stand-in for the registry.
    /// </summary>
    private sealed class FakeUserEnvironment : IUserEnvironment
    {
        /// <summary>Gets the variables currently set.</summary>
        public Dictionary<string, UserTempValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets the number of broadcasts sent.</summary>
        public int Broadcasts { get; private set; }

        /// <inheritdoc />
        public UserTempValue? Read(string name) => Values.GetValueOrDefault(name);

        /// <inheritdoc />
        public void Write(string name, string value, RegistryValueKind kind) => Values[name] = new(value, kind);

        /// <inheritdoc />
        public void Delete(string name) => Values.Remove(name);

        /// <inheritdoc />
        public void Broadcast() => Broadcasts++;
    }
}
