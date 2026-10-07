using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

public sealed class LowMemoryMonitorTests
{
    private const ulong Mb = 1024UL * 1024;
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_PlentyOfMemory_ReportsNothing()
    {
        var monitor = new LowMemoryMonitor();

        Assert.Equal(LowMemoryTransition.None, monitor.Evaluate(4096 * Mb, Start));
    }

    [Fact]
    public void Evaluate_ExactlyAtTheWarnThreshold_ReportsNothing()
    {
        var monitor = new LowMemoryMonitor();

        Assert.Equal(LowMemoryTransition.None, monitor.Evaluate(LowMemoryMonitor.WarnBelowBytes, Start));
    }

    [Fact]
    public void Evaluate_BelowTheWarnThreshold_Warns()
    {
        var monitor = new LowMemoryMonitor();

        Assert.Equal(LowMemoryTransition.Warn, monitor.Evaluate(LowMemoryMonitor.WarnBelowBytes - 1, Start));
    }

    [Fact]
    public void Evaluate_StillLowOnTheNextReading_WarnsOnlyOnce()
    {
        var monitor = new LowMemoryMonitor();
        monitor.Evaluate(300 * Mb, Start);

        Assert.Equal(LowMemoryTransition.None, monitor.Evaluate(280 * Mb, Start + TimeSpan.FromSeconds(10)));
        Assert.Equal(LowMemoryTransition.None, monitor.Evaluate(200 * Mb, Start + TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Evaluate_RisingBetweenTheThresholds_DoesNotRecover()
    {
        // 600 MB is above the warn line but below the recovery line: still "low", no flapping.
        var monitor = new LowMemoryMonitor();
        monitor.Evaluate(300 * Mb, Start);

        Assert.Equal(LowMemoryTransition.None, monitor.Evaluate(600 * Mb, Start + TimeSpan.FromSeconds(10)));
        Assert.Equal(LowMemoryTransition.None, monitor.Evaluate(400 * Mb, Start + TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Evaluate_RisingAboveTheRecoverThreshold_ReportsRecoveredOnce()
    {
        var monitor = new LowMemoryMonitor();
        monitor.Evaluate(300 * Mb, Start);

        Assert.Equal(LowMemoryTransition.Recovered, monitor.Evaluate(LowMemoryMonitor.RecoverAboveBytes + 1, Start + TimeSpan.FromSeconds(10)));
        Assert.Equal(LowMemoryTransition.None, monitor.Evaluate(2048 * Mb, Start + TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Evaluate_ExactlyAtTheRecoverThreshold_DoesNotRecover()
    {
        var monitor = new LowMemoryMonitor();
        monitor.Evaluate(300 * Mb, Start);

        Assert.Equal(LowMemoryTransition.None, monitor.Evaluate(LowMemoryMonitor.RecoverAboveBytes, Start + TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Evaluate_LowAgainWithinTheCooldown_DoesNotWarnAgain()
    {
        var monitor = new LowMemoryMonitor();
        monitor.Evaluate(300 * Mb, Start);
        monitor.Evaluate(2048 * Mb, Start + TimeSpan.FromMinutes(1));

        var result = monitor.Evaluate(300 * Mb, Start + TimeSpan.FromMinutes(2));

        Assert.Equal(LowMemoryTransition.None, result);
    }

    [Fact]
    public void Evaluate_LowAgainAfterTheCooldown_WarnsAgain()
    {
        var monitor = new LowMemoryMonitor();
        monitor.Evaluate(300 * Mb, Start);
        monitor.Evaluate(2048 * Mb, Start + TimeSpan.FromMinutes(1));

        var result = monitor.Evaluate(300 * Mb, Start + LowMemoryMonitor.WarnCooldown);

        Assert.Equal(LowMemoryTransition.Warn, result);
    }

    [Fact]
    public void Evaluate_RecoveryAfterASuppressedWarning_StillReportsRecovered()
    {
        // The status bar may still show the earlier report; the recovery must be able to clear it.
        var monitor = new LowMemoryMonitor();
        monitor.Evaluate(300 * Mb, Start);
        monitor.Evaluate(2048 * Mb, Start + TimeSpan.FromMinutes(1));
        monitor.Evaluate(300 * Mb, Start + TimeSpan.FromMinutes(2));

        var result = monitor.Evaluate(2048 * Mb, Start + TimeSpan.FromMinutes(3));

        Assert.Equal(LowMemoryTransition.Recovered, result);
    }
}
