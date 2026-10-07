using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

public sealed class NotificationCooldownTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private NotificationCooldown Create() => new(Cooldown, () => _now);

    [Fact]
    public void TryAcquire_FirstTime_ReturnsTrue()
    {
        Assert.True(Create().TryAcquire("R:|DiskFull"));
    }

    [Fact]
    public void TryAcquire_WithinCooldown_ReturnsFalse()
    {
        var cooldown = Create();
        cooldown.TryAcquire("R:|DiskFull");

        _now += TimeSpan.FromMinutes(9);

        Assert.False(cooldown.TryAcquire("R:|DiskFull"));
    }

    [Fact]
    public void TryAcquire_AfterCooldown_ReturnsTrueAgain()
    {
        var cooldown = Create();
        cooldown.TryAcquire("R:|DiskFull");

        _now += Cooldown;

        Assert.True(cooldown.TryAcquire("R:|DiskFull"));
    }

    [Fact]
    public void TryAcquire_RefusedCall_DoesNotRestartTheCooldown()
    {
        var cooldown = Create();
        cooldown.TryAcquire("R:|DiskFull");
        _now += TimeSpan.FromMinutes(6);
        Assert.False(cooldown.TryAcquire("R:|DiskFull"));

        _now += TimeSpan.FromMinutes(5);

        // 11 minutes since the one that was let through, even though only 5 since the refusal.
        Assert.True(cooldown.TryAcquire("R:|DiskFull"));
    }

    [Fact]
    public void TryAcquire_DifferentKeys_AreIndependent()
    {
        var cooldown = Create();
        cooldown.TryAcquire("R:|DiskFull");

        Assert.True(cooldown.TryAcquire("R:|LowMemory"));
        Assert.True(cooldown.TryAcquire("S:|DiskFull"));
    }
}
