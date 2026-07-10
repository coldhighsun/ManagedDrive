using ManagedDrive.Core;

namespace ManagedDrive.Tests;

public sealed class RamDiskCapacityTests
{
    [Fact]
    public void ResolveEffectiveCapacity_ActualUsedExceedsConfigured_RaisesToActualUsed()
    {
        var result = RamDisk.ResolveEffectiveCapacity(configuredCapacity: 10_000, actualUsed: 25_000);
        Assert.Equal(25_000UL, result);
    }

    [Fact]
    public void ResolveEffectiveCapacity_ActualUsedWithinConfigured_KeepsConfigured()
    {
        var result = RamDisk.ResolveEffectiveCapacity(configuredCapacity: 25_000, actualUsed: 10_000);
        Assert.Equal(25_000UL, result);
    }

    [Fact]
    public void ResolveEffectiveCapacity_ActualUsedEqualsConfigured_KeepsConfigured()
    {
        var result = RamDisk.ResolveEffectiveCapacity(configuredCapacity: 10_000, actualUsed: 10_000);
        Assert.Equal(10_000UL, result);
    }
}
