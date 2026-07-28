using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

public class UpdateCheckServiceTests
{
    [Theory]
    [InlineData("1.9.0-alpha.1", "1.9.0")]   // prerelease precedes its formal release
    [InlineData("1.9.0", "1.9.1")]           // higher patch
    [InlineData("v1.9.0-alpha.1", "v1.9.0")] // v-prefix tolerated on both sides
    [InlineData("1.9.0-alpha.1+abc123", "1.9.0")] // +build metadata ignored
    public void IsNewerFormalRelease_LatestIsNewer_ReturnsTrue(string running, string latest)
    {
        Assert.True(UpdateCheckService.IsNewerFormalRelease(running, latest));
    }

    [Theory]
    [InlineData("1.9.0", "1.9.0")]            // identical formal versions
    [InlineData("1.9.0", "1.8.9")]            // latest is older
    [InlineData("1.10.0-alpha.1", "1.9.0")]   // prerelease of a newer core still outranks older release
    [InlineData("1.9", "1.9.0")]              // 1.9 and 1.9.0 are the same core
    public void IsNewerFormalRelease_LatestNotNewer_ReturnsFalse(string running, string latest)
    {
        Assert.False(UpdateCheckService.IsNewerFormalRelease(running, latest));
    }

    [Theory]
    [InlineData("garbage", "1.9.0")]
    [InlineData("", "1.9.0")]
    [InlineData("1.9.0", "not-a-version")]
    public void IsNewerFormalRelease_UnparseableVersion_ReturnsFalse(string running, string latest)
    {
        Assert.False(UpdateCheckService.IsNewerFormalRelease(running, latest));
    }
}
