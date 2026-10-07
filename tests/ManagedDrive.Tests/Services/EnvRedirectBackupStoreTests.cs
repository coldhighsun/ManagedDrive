using ManagedDrive.App.Models;
using ManagedDrive.App.Services;
using Microsoft.Win32;

namespace ManagedDrive.Tests;

public sealed class EnvRedirectBackupStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    private string FilePath => Path.Combine(_dir, "env-redirects.json");

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
    public void Read_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(new EnvRedirectBackupStore(FilePath).Read());
    }

    [Fact]
    public void WriteThenRead_RoundTripsBackupsAndLeavesNoTempFile()
    {
        var store = new EnvRedirectBackupStore(FilePath);
        EnvRedirectBackup[] backups =
        [
            new() { Variable = "A", MountPoint = "R:", AppliedValue = @"R:\a", OriginalText = @"%USERPROFILE%\a", OriginalKind = RegistryValueKind.ExpandString },
            new() { Variable = "B", MountPoint = "R:", AppliedValue = @"R:\b" },
        ];

        Assert.True(store.Write(backups));
        var loaded = store.Read();

        Assert.Equal(backups, loaded);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Write_OverExistingFile_ReplacesIt()
    {
        var store = new EnvRedirectBackupStore(FilePath);
        store.Write([new() { Variable = "A" }]);

        store.Write([]);

        Assert.Empty(store.Read());
    }

    [Fact]
    public void Read_CorruptFile_ReturnsEmptyAndMovesItAsideOnce()
    {
        var store = new EnvRedirectBackupStore(FilePath);
        File.WriteAllText(FilePath, "{ not json");

        var loaded = store.Read();

        Assert.Empty(loaded);
        Assert.Single(Directory.GetFiles(_dir, "env-redirects.json.corrupt-*"));
        Assert.False(File.Exists(FilePath));
        Assert.Empty(store.Read());
        Assert.Single(Directory.GetFiles(_dir, "env-redirects.json.corrupt-*"));
    }

    [Fact]
    public void Read_JsonNullEntries_AreDropped()
    {
        File.WriteAllText(new EnvRedirectBackupStore(FilePath) is { } ? FilePath : FilePath, "[ null, { \"Variable\": \"A\" } ]");

        var loaded = new EnvRedirectBackupStore(FilePath).Read();

        Assert.Equal("A", Assert.Single(loaded).Variable);
    }
}

public sealed class CoalescingBroadcasterTests
{
    [Fact]
    public void Request_DoesNotBlockWhileTheSendIsSlow()
    {
        using var release = new ManualResetEventSlim();
        var broadcaster = new CoalescingBroadcaster(() => release.Wait(TimeSpan.FromSeconds(10)));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        broadcaster.Request();
        var elapsed = watch.Elapsed;
        release.Set();

        Assert.True(elapsed < TimeSpan.FromSeconds(2));
        Assert.True(broadcaster.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Request_Burst_IsMergedIntoAtMostTwoSends()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sends = 0;
        var broadcaster = new CoalescingBroadcaster(() =>
        {
            Interlocked.Increment(ref sends);
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });

        broadcaster.Request();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        for (var i = 0; i < 50; i++)
        {
            broadcaster.Request();
        }

        release.Set();
        Assert.True(broadcaster.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, sends);
    }

    [Fact]
    public void Wait_NothingRequested_ReturnsImmediately()
    {
        Assert.True(new CoalescingBroadcaster(() => { }).Wait(TimeSpan.Zero));
    }

    [Fact]
    public void Wait_SendStillRunning_ReturnsFalseAfterTheTimeout()
    {
        using var release = new ManualResetEventSlim();
        var broadcaster = new CoalescingBroadcaster(() => release.Wait(TimeSpan.FromSeconds(10)));
        broadcaster.Request();

        var finished = broadcaster.Wait(TimeSpan.FromMilliseconds(50));
        release.Set();

        Assert.False(finished);
        Assert.True(broadcaster.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Request_SendThrows_DoesNotBreakLaterRequests()
    {
        var sends = 0;
        var broadcaster = new CoalescingBroadcaster(() =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                throw new InvalidOperationException("boom");
            }
        });

        broadcaster.Request();
        Assert.True(broadcaster.Wait(TimeSpan.FromSeconds(5)));
        broadcaster.Request();

        Assert.True(broadcaster.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, sends);
    }
}
