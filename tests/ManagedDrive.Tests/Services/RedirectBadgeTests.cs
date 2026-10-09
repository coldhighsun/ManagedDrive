using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="RedirectBadgeTracker"/> and <see cref="RedirectBadgeText"/>.
/// </summary>
public sealed class RedirectBadgeTests
{
    /// <summary>
    /// The tooltip lists every active item below the header.
    /// </summary>
    [Fact]
    public void Build_ActivePresetsAndCustom_ListsEachOnItsOwnLine()
    {
        var active = new ActiveRedirects { PresetIds = ["temp", "nuget"], CustomVariables = ["GOPATH"] };

        var text = RedirectBadgeText.Build(active, "Header", id => $"P:{id}", variable => $"C:{variable}");

        Assert.Equal("Header\nP:temp\nP:nuget\nC:GOPATH", text);
    }

    /// <summary>
    /// With nothing redirected there is no tooltip text.
    /// </summary>
    [Fact]
    public void Build_NothingActive_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, RedirectBadgeText.Build(new(), "Header", id => id, v => v));
    }

    /// <summary>
    /// The tracker reports a change once, then nothing until the environment or the disk changes.
    /// </summary>
    [Fact]
    public void Update_TempPointsIntoDisk_ReportsChangeOnlyOnce()
    {
        var now = 1_000L;
        var cache = new UserEnvVarCache(
            name => name is "TEMP" or "TMP" ? @"R:\Temp" : null, () => now, TimeSpan.FromSeconds(5));
        var tracker = new RedirectBadgeTracker(cache);
        IReadOnlyList<EnvRedirect> redirects = [];

        var first = tracker.Update("R:", redirects);
        var second = tracker.Update("R:", redirects);
        now += 5_000;
        var afterExpiry = tracker.Update("R:", redirects);

        Assert.True(first);
        Assert.False(second);
        Assert.False(afterExpiry);
        Assert.Equal(["temp"], tracker.Active.PresetIds);
    }

    /// <summary>
    /// When the environment stops pointing into the disk, the next update after invalidation reports it.
    /// </summary>
    [Fact]
    public void Update_EnvironmentRestored_ClearsActive()
    {
        var temp = @"R:\Temp";
        var cache = new UserEnvVarCache(_ => temp, () => 1_000L, TimeSpan.FromSeconds(5));
        var tracker = new RedirectBadgeTracker(cache);
        tracker.Update("R:", []);

        temp = @"C:\Users\u\AppData\Local\Temp";
        cache.Invalidate();
        var changed = tracker.Update("R:", []);

        Assert.True(changed);
        Assert.True(tracker.Active.IsEmpty);
    }

    /// <summary>
    /// A disk whose TEMP alone points into it still counts as the temp disk, as before.
    /// </summary>
    [Fact]
    public void Update_OnlyTempPointsIntoDisk_StillReportsTemp()
    {
        var cache = new UserEnvVarCache(
            name => name == "TEMP" ? @"R:\Temp" : @"C:\Users\u\AppData\Local\Temp", () => 1_000L, TimeSpan.FromSeconds(5));
        var tracker = new RedirectBadgeTracker(cache);

        tracker.Update("R:", []);

        Assert.Equal(["temp"], tracker.Active.PresetIds);
    }

    /// <summary>
    /// If working out the answer fails, the tracker does not remember the inputs as handled, so the next update retries.
    /// </summary>
    [Fact]
    public void Update_ReadThrows_RetriesNextTime()
    {
        var fail = true;
        var cache = new UserEnvVarCache(
            _ => fail ? throw new InvalidOperationException() : @"R:\Temp", () => 1_000L, TimeSpan.FromSeconds(5));
        var tracker = new RedirectBadgeTracker(cache);

        Assert.Throws<InvalidOperationException>(() => tracker.Update("R:", []));
        fail = false;
        cache.Invalidate();

        Assert.True(tracker.Update("R:", []));
        Assert.Equal(["temp"], tracker.Active.PresetIds);
    }
}
