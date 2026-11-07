namespace ManagedDrive.Tests;

public sealed class SnapshotSizeLimitsTests
{
    private const ulong Mb = 1024UL * 1024;
    private const ulong Gb = 1024UL * Mb;

    [Fact]
    public void ResolveMaxBytes_UsesFreeSpace_WhenKnown()
    {
        Assert.Equal(500 * Gb, SnapshotSizeLimits.ResolveMaxBytes(500 * Gb));
    }

    [Fact]
    public void ResolveMaxBytes_IsNotCappedByFreeSpaceBeingLargerThanTheFallback()
    {
        Assert.Equal(4096 * Gb, SnapshotSizeLimits.ResolveMaxBytes(4096 * Gb));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1024UL)]
    [InlineData(Mb - 1)]
    public void ResolveMaxBytes_NeverDropsBelowOneMb(ulong freeBytes)
    {
        Assert.Equal(Mb, SnapshotSizeLimits.ResolveMaxBytes(freeBytes));
    }

    [Fact]
    public void ResolveMaxBytes_KeepsSmallFreeSpaceAccurate()
    {
        Assert.Equal(300 * Mb, SnapshotSizeLimits.ResolveMaxBytes(300 * Mb));
    }

    [Fact]
    public void ResolveMaxBytes_FallsBackToOneTerabyte_WhenFreeSpaceUnknown()
    {
        Assert.Equal(1024 * Gb, SnapshotSizeLimits.ResolveMaxBytes(null));
    }
}
