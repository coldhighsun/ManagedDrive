using System.Diagnostics;
using ManagedDrive.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ManagedDrive.Tests;

public sealed class SessionEndingSaveHandlerTests
{
    /// <summary>
    /// Creates a handler over an empty mount manager.
    /// </summary>
    private static SessionEndingSaveHandler CreateHandler(Action? beforeSave, TimeSpan? saveTimeout = null) => new(
        new MountManager(),
        () => IntPtr.Zero,
        NullLogger<SessionEndingSaveHandler>.Instance,
        beforeSave,
        saveTimeout);

    /// <summary>
    /// Creates a handler that runs the given saves instead of the mounted disks'.
    /// </summary>
    private static SessionEndingSaveHandler CreateHandlerWithSaves(
        IReadOnlyList<Action> saves,
        TimeSpan? saveTimeout = null,
        TimeSpan? followUpTimeout = null,
        ShutdownBlockCounter? blocks = null) => new(
        new MountManager(),
        () => IntPtr.Zero,
        NullLogger<SessionEndingSaveHandler>.Instance,
        beforeSave: null,
        saveTimeout,
        followUpTimeout,
        () => saves,
        blocks);

    /// <summary>
    /// Creates a handler whose save list comes from the given function.
    /// </summary>
    private static SessionEndingSaveHandler CreateHandlerWithSource(
        Func<IReadOnlyList<Action>> source, Action? beforeSave = null) => new(
        new MountManager(),
        () => IntPtr.Zero,
        NullLogger<SessionEndingSaveHandler>.Instance,
        beforeSave,
        saveTimeout: null,
        followUpTimeout: null,
        source);

    /// <summary>
    /// Arguments for a session-ending notification.
    /// </summary>
    private static readonly SessionEndingEventArgs Args = new(SessionEndReasons.SystemShutdown);
    /// <summary>
    /// Arguments for a session-ended notification.
    /// </summary>
    private static readonly SessionEndedEventArgs EndedArgs = new(SessionEndReasons.SystemShutdown);

    [Fact]
    public void OnSessionEnded_RunsTheBeforeSaveStepOnce()
    {
        var calls = 0;

        CreateHandler(() => calls++).OnSessionEnded(this, EndedArgs);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void OnSessionEnding_OnlyAsked_DoesNotRestoreTheEnvironment()
    {
        var calls = 0;

        CreateHandler(() => calls++).OnSessionEnding(this, Args);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void SaveAllDisks_Called_DoesNotRestoreTheEnvironment()
    {
        var calls = 0;

        CreateHandler(() => calls++).SaveAllDisks();

        Assert.Equal(0, calls);
    }

    [Fact]
    public void OnSessionEnded_BeforeSaveThrows_DoesNotPropagate()
    {
        var handler = CreateHandler(() => throw new InvalidOperationException("registry unavailable"));

        var exception = Record.Exception(() => handler.OnSessionEnded(this, EndedArgs));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StartSaves_AllFinishInTime_RunsEach()
    {
        var done = 0;
        Action[] saves = [() => Interlocked.Increment(ref done), () => Interlocked.Increment(ref done)];

        var all = SessionEndingSaveHandler.StartSaves(saves, _ => { });

        await all.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, done);
    }

    [Fact]
    public async Task StartSaves_OneSaveThrows_ReportsItAndStillRunsTheOthers()
    {
        var done = 0;
        var failures = new List<Exception>();
        Action[] saves = [() => throw new InvalidOperationException("disk gone"), () => Interlocked.Increment(ref done)];

        var all = SessionEndingSaveHandler.StartSaves(saves, failures.Add);

        await all.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, done);
        Assert.Equal("disk gone", Assert.Single(failures).Message);
    }

    [Fact]
    public async Task StartSaves_FailureCallbackThrows_FaultsTheReturnedTask()
    {
        Action[] saves = [() => throw new InvalidOperationException("disk gone")];

        var all = SessionEndingSaveHandler.StartSaves(saves, _ => throw new InvalidOperationException("logger gone"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => all.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SaveAllDisks_WithSaves_RunsEveryOneAndKeepsGoingWhenOneThrows()
    {
        var done = 0;
        var handler = CreateHandlerWithSaves(
            [() => throw new InvalidOperationException("disk gone"), () => Interlocked.Increment(ref done), () => Interlocked.Increment(ref done)]);

        handler.SaveAllDisks();

        Assert.Equal(2, done);
    }

    [Fact]
    public void SaveAllDisks_CalledTwice_SavesAgainBecauseTheSessionEndCanBeVetoed()
    {
        var done = 0;
        var handler = CreateHandlerWithSaves([() => Interlocked.Increment(ref done)]);

        handler.SaveAllDisks();
        handler.SaveAllDisks();

        Assert.Equal(2, done);
    }

    [Fact]
    public void SaveAllDisks_ASaveOutlastsTheTimeout_ReturnsAfterTheTimeoutAndLaterCallsWaitOnlyBrieflyWithoutQueuingMoreSaves()
    {
        using var release = new ManualResetEventSlim();
        var started = 0;
        var handler = CreateHandlerWithSaves(
            [() =>
            {
                Interlocked.Increment(ref started);
                release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }],
            saveTimeout: TimeSpan.FromSeconds(1),
            followUpTimeout: TimeSpan.FromMilliseconds(20));

        var first = Stopwatch.StartNew();
        handler.SaveAllDisks();
        first.Stop();
        var second = Stopwatch.StartNew();
        handler.SaveAllDisks();
        handler.SaveAllDisks();
        second.Stop();
        release.Set();

        Assert.True(first.Elapsed >= TimeSpan.FromMilliseconds(900), $"first call took {first.Elapsed}");
        Assert.True(second.Elapsed < TimeSpan.FromMilliseconds(700), $"two follow-up calls took {second.Elapsed}");
        Assert.Equal(1, started);
    }

    [Fact]
    public void SaveAllDisks_FinalCallWhileASaveIsRunning_WaitsTheFullTimeout()
    {
        using var release = new ManualResetEventSlim();
        var handler = CreateHandlerWithSaves(
            [() => release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)],
            saveTimeout: TimeSpan.FromMilliseconds(500),
            followUpTimeout: TimeSpan.FromMilliseconds(20));
        handler.SaveAllDisks();

        var watch = Stopwatch.StartNew();
        handler.OnSessionEnded(this, EndedArgs);
        watch.Stop();
        release.Set();

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(400), $"final call took {watch.Elapsed}");
    }

    [Fact]
    public void SaveAllDisks_FinalCallSharesOneTimeoutBetweenTheEarlierSaveAndItsOwn()
    {
        using var release = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var calls = 0;
        var handler = CreateHandlerWithSaves(
            [() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                }
                else
                {
                    releaseSecond.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                }
            }],
            saveTimeout: TimeSpan.FromMilliseconds(800),
            followUpTimeout: TimeSpan.FromMilliseconds(20));
        handler.SaveAllDisks();
        _ = Task.Run(() =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(300));
            release.Set();
        }, TestContext.Current.CancellationToken);

        var watch = Stopwatch.StartNew();
        handler.OnSessionEnded(this, EndedArgs);
        watch.Stop();
        releaseSecond.Set();

        Assert.Equal(2, calls);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1300), $"final call took {watch.Elapsed}");
    }

    [Fact]
    public void SaveAllDisks_AllSavesFinish_ReleasesTheBlockAtOnce()
    {
        var counts = new int[2];
        var blocks = new ShutdownBlockCounter(() => ++counts[0] > 0, () => counts[1]++);
        var handler = CreateHandlerWithSaves([() => { }], blocks: blocks);

        handler.SaveAllDisks();

        Assert.Equal(1, counts[0]);
        Assert.Equal(1, counts[1]);
    }

    [Fact]
    public void SaveAllDisks_ASaveOutlastsTheTimeout_KeepsTheBlockUntilItFinishes()
    {
        using var release = new ManualResetEventSlim();
        var cleared = 0;
        var blocks = new ShutdownBlockCounter(() => true, () => Interlocked.Increment(ref cleared));
        var handler = CreateHandlerWithSaves(
            [() => release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)],
            saveTimeout: TimeSpan.FromMilliseconds(100),
            blocks: blocks);

        handler.SaveAllDisks();
        var heldAfterTimeout = Volatile.Read(ref cleared) == 0;
        release.Set();
        var released = SpinWait.SpinUntil(() => Volatile.Read(ref cleared) == 1, TimeSpan.FromSeconds(10));

        Assert.True(heldAfterTimeout);
        Assert.True(released);
    }

    [Fact]
    public void SaveAllDisks_AfterAnEarlierSaveFinishes_SavesAgain()
    {
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var handler = CreateHandlerWithSaves(
            [() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                }
            }],
            saveTimeout: TimeSpan.FromMilliseconds(200),
            followUpTimeout: TimeSpan.FromSeconds(10));

        handler.SaveAllDisks();
        release.Set();
        handler.SaveAllDisks();

        Assert.Equal(2, calls);
    }

    [Fact]
    public void OnSessionEnded_Called_RestoresTheEnvironmentBeforeSaving()
    {
        var order = new List<string>();
        var handler = CreateHandlerWithSource(() => [() => order.Add("save")], () => order.Add("environment"));

        handler.OnSessionEnded(this, EndedArgs);

        Assert.Equal(["environment", "save"], order);
    }

    [Fact]
    public void OnSessionEnding_BuildingTheSaveListThrows_DoesNotPropagate()
    {
        var handler = CreateHandlerWithSource(() => throw new ObjectDisposedException("disk"));

        var exception = Record.Exception(() => handler.OnSessionEnding(this, Args));

        Assert.Null(exception);
    }

    [Fact]
    public void OnSessionEnded_BuildingTheSaveListThrows_StillRestoresTheEnvironmentAndDoesNotPropagate()
    {
        var restored = 0;
        var handler = CreateHandlerWithSource(() => throw new ObjectDisposedException("disk"), () => restored++);

        var exception = Record.Exception(() => handler.OnSessionEnded(this, EndedArgs));

        Assert.Null(exception);
        Assert.Equal(1, restored);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void SaveUnlessCancelled_Cancelled_SavesOnlyWhenNotCancelled(bool cancelled, int expectedSaves)
    {
        var done = 0;
        var handler = CreateHandlerWithSaves([() => Interlocked.Increment(ref done)]);

        handler.SaveUnlessCancelled(cancelled);

        Assert.Equal(expectedSaves, done);
    }

    [Fact]
    public void DefaultSaveTimeout_Value_CoversALargeImageSave()
    {
        // A 3 GB image took ~20 s to save; the old 10 s budget cut it off.
        Assert.True(SessionEndingSaveHandler.DefaultSaveTimeout >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void OnSessionEnding_WithoutABeforeSaveStep_Completes()
    {
        var exception = Record.Exception(() => CreateHandler(null).OnSessionEnding(this, Args));

        Assert.Null(exception);
    }
}

public sealed class TempDirCompatCheckerMountPointTests
{
    [Theory]
    [InlineData(@"R:\Temp", true)]
    [InlineData(@"r:\", true)]
    [InlineData(@"C:\mnt\ramdisk\Temp", true)]
    [InlineData(@"C:\mnt\ramdisk2\Temp", false)]
    [InlineData(@"C:\Users\USER\AppData\Local\Temp", false)]
    [InlineData(@"S:\Temp", false)]
    public void IsPathOnAnyMountPoint_Path_MatchesDriveLettersAndDirectoryMountPoints(string path, bool expected)
    {
        var result = TempDirCompatChecker.IsPathOnAnyMountPoint(path, ["R:", @"C:\mnt\ramdisk"]);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsPathOnAnyMountPoint_NoMountPoints_ReturnsFalse()
    {
        Assert.False(TempDirCompatChecker.IsPathOnAnyMountPoint(@"R:\Temp", []));
    }
}
