using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="UserEnvVarCache"/>.
/// </summary>
public sealed class UserEnvVarCacheTests
{
    /// <summary>
    /// Reads of the same variable inside the cache duration hit the system once.
    /// </summary>
    [Fact]
    public void Get_SameVariableWithinDuration_ReadsOnlyOnce()
    {
        var reads = 0;
        var now = 1_000L;
        var cache = new UserEnvVarCache(_ => $"v{++reads}", () => now, TimeSpan.FromSeconds(5));

        var first = cache.Get("NUGET_PACKAGES");
        now += 4_999;
        var second = cache.Get("nuget_packages");

        Assert.Equal("v1", first);
        Assert.Equal("v1", second);
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// Different variables are cached independently.
    /// </summary>
    [Fact]
    public void Get_DifferentVariables_ReadsEach()
    {
        var cache = new UserEnvVarCache(name => $"value-of-{name}", () => 1_000L, TimeSpan.FromSeconds(5));

        Assert.Equal("value-of-A", cache.Get("A"));
        Assert.Equal("value-of-B", cache.Get("B"));
    }

    /// <summary>
    /// After the duration the next read goes to the system again.
    /// </summary>
    [Fact]
    public void Get_AfterDuration_ReadsAgain()
    {
        var reads = 0;
        var now = 1_000L;
        var cache = new UserEnvVarCache(_ => $"v{++reads}", () => now, TimeSpan.FromSeconds(5));

        cache.Get("A");
        now += 5_000;

        Assert.Equal("v2", cache.Get("A"));
    }

    /// <summary>
    /// A write by the app invalidates the cache so the change is seen immediately.
    /// </summary>
    [Fact]
    public void Invalidate_ThenGet_ReadsAgainWithinDuration()
    {
        var current = "old";
        var cache = new UserEnvVarCache(_ => current, () => 1_000L, TimeSpan.FromSeconds(5));
        cache.Get("A");

        current = "new";
        cache.Invalidate();

        Assert.Equal("new", cache.Get("A"));
    }

    /// <summary>
    /// A variable that isn't set is cached like any other, without reading again on every call.
    /// </summary>
    [Fact]
    public void Get_VariableNotSet_ReturnsNullAndCachesIt()
    {
        var reads = 0;
        var cache = new UserEnvVarCache(_ =>
        {
            reads++;
            return null;
        }, () => 1_000L, TimeSpan.FromSeconds(5));

        Assert.Null(cache.Get("A"));
        Assert.Null(cache.Get("A"));
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// The generation moves when a cached value changed or the cache was invalidated, and stays put
    /// when an expiry re-reads the same values.
    /// </summary>
    [Fact]
    public void Generation_ChangesOnlyWhenValuesChangeOrOnInvalidate()
    {
        var now = 1_000L;
        var value = "v";
        var cache = new UserEnvVarCache(_ => value, () => now, TimeSpan.FromSeconds(5));
        cache.Get("A");

        var first = cache.Generation;
        now += 5_000;
        var sameValueAfterExpiry = cache.Generation;
        value = "w";
        now += 5_000;
        var changedAfterExpiry = cache.Generation;
        cache.Invalidate();
        var afterInvalidate = cache.Generation;

        Assert.Equal(first, sameValueAfterExpiry);
        Assert.NotEqual(first, changedAfterExpiry);
        Assert.NotEqual(changedAfterExpiry, afterInvalidate);
        Assert.Equal("w", cache.Get("A"));
    }

    /// <summary>
    /// If re-reading fails on expiry the cache stays stale, so the next call retries at once instead
    /// of serving old values as fresh for another interval.
    /// </summary>
    [Fact]
    public void Get_ReadThrowsOnExpiry_RetriesOnNextCall()
    {
        var now = 1_000L;
        var fail = false;
        var value = "old";
        var cache = new UserEnvVarCache(
            _ => fail ? throw new InvalidOperationException() : value, () => now, TimeSpan.FromSeconds(5));
        cache.Get("A");
        now += 5_000;
        fail = true;

        Assert.Throws<InvalidOperationException>(() => cache.Get("A"));
        fail = false;
        value = "new";

        Assert.Equal("new", cache.Get("A"));
    }

    /// <summary>
    /// A variable pointing at the disk's folder is recognised, ignoring case and a trailing slash.
    /// </summary>
    [Fact]
    public void PointsInto_VariableSetToDiskFolder_ReturnsTrue()
    {
        var cache = new UserEnvVarCache(_ => @"r:\NuGet\", () => 1_000L, TimeSpan.FromSeconds(5));

        Assert.True(cache.PointsInto(@"R:\", new() { Variable = "NUGET_PACKAGES", SubPath = "nuget" }));
    }

    /// <summary>
    /// A variable that is unset or points elsewhere does not count.
    /// </summary>
    /// <param name="value">The variable's value.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\Users\u\.nuget\packages")]
    public void PointsInto_VariableUnsetOrElsewhere_ReturnsFalse(string? value)
    {
        var cache = new UserEnvVarCache(_ => value, () => 1_000L, TimeSpan.FromSeconds(5));

        Assert.False(cache.PointsInto(@"R:\", new() { Variable = "NUGET_PACKAGES", SubPath = "nuget" }));
    }

    /// <summary>
    /// A redirection whose sub-path is not valid never matches, whatever the variable holds.
    /// </summary>
    [Fact]
    public void PointsInto_InvalidSubPath_ReturnsFalse()
    {
        var cache = new UserEnvVarCache(_ => @"R:\..", () => 1_000L, TimeSpan.FromSeconds(5));

        Assert.False(cache.PointsInto(@"R:\", new() { Variable = "X", SubPath = ".." }));
    }
}
