using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="UserTempCache"/>.
/// </summary>
public sealed class UserTempCacheTests
{
    /// <summary>
    /// Reads inside the cache duration reuse the first read instead of hitting the system again.
    /// </summary>
    [Fact]
    public void Get_WithinDuration_ReadsOnlyOnce()
    {
        var reads = 0;
        var now = 1_000L;
        var cache = new UserTempCache(() => $"value{++reads}", () => now, TimeSpan.FromSeconds(5));

        var first = cache.Get();
        now += 4_999;
        var second = cache.Get();

        Assert.Equal("value1", first);
        Assert.Equal("value1", second);
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// Once the duration has passed, the next read goes to the system again.
    /// </summary>
    [Fact]
    public void Get_AfterDuration_ReadsAgain()
    {
        var reads = 0;
        var now = 1_000L;
        var cache = new UserTempCache(() => $"value{++reads}", () => now, TimeSpan.FromSeconds(5));

        cache.Get();
        now += 5_000;

        Assert.Equal("value2", cache.Get());
    }

    /// <summary>
    /// A write to TEMP by the app invalidates the cache, so the change is seen immediately.
    /// </summary>
    [Fact]
    public void Invalidate_ThenGet_ReadsAgainWithinDuration()
    {
        var current = @"C:\Users\u\AppData\Local\Temp";
        var now = 1_000L;
        var cache = new UserTempCache(() => current, () => now, TimeSpan.FromSeconds(5));
        cache.Get();

        current = @"R:\Temp";
        cache.Invalidate();

        Assert.Equal(@"R:\Temp", cache.Get());
    }

    /// <summary>
    /// A TEMP value that isn't set is cached like any other, without reading again on every call.
    /// </summary>
    [Fact]
    public void Get_ValueNotSet_ReturnsNullAndCachesIt()
    {
        var reads = 0;
        var cache = new UserTempCache(() =>
        {
            reads++;
            return null;
        }, () => 1_000L, TimeSpan.FromSeconds(5));

        Assert.Null(cache.Get());
        Assert.Null(cache.Get());
        Assert.Equal(1, reads);
    }
}
