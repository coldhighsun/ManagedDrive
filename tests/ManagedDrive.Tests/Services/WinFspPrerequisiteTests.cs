using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests the version comparison of <see cref="WinFspPrerequisite"/>.
/// </summary>
public sealed class WinFspPrerequisiteTests
{
    /// <summary>
    /// No installed runtime is reported as missing.
    /// </summary>
    [Fact]
    public void Evaluate_NoInstall_IsMissing() =>
        Assert.Equal(WinFspStatus.Missing, WinFspPrerequisite.Evaluate(null));

    /// <summary>
    /// The exact required version is accepted.
    /// </summary>
    [Fact]
    public void Evaluate_RequiredVersion_IsOk() =>
        Assert.Equal(WinFspStatus.Ok, WinFspPrerequisite.Evaluate(new Version(2, 2, 26286)));

    /// <summary>
    /// The previous beta, other builds and other minor/major versions are mismatches.
    /// </summary>
    [Theory]
    [InlineData(2, 2, 26215)]
    [InlineData(2, 1, 25156)]
    [InlineData(2, 2, 26285)]
    public void Evaluate_OlderVersion_IsMismatch(int major, int minor, int build) =>
        Assert.Equal(WinFspStatus.VersionMismatch, WinFspPrerequisite.Evaluate(new Version(major, minor, build)));

    /// <summary>
    /// Newer installs are reported as such, so no downgrade is offered.
    /// </summary>
    [Theory]
    [InlineData(2, 2, 26287)]
    [InlineData(2, 3, 100)]
    [InlineData(3, 0, 1)]
    public void Evaluate_NewerVersion_IsNewer(int major, int minor, int build) =>
        Assert.Equal(WinFspStatus.Newer, WinFspPrerequisite.Evaluate(new Version(major, minor, build)));

    /// <summary>
    /// Only a missing or an older, not yet declined install prompts; a declined pair stays quiet until either version changes.
    /// </summary>
    [Fact]
    public void ShouldPrompt_FollowsStatusAndSkipKey()
    {
        var older = new Version(2, 2, 26215);
        var olderCheck = new WinFspCheckResult(WinFspStatus.VersionMismatch, older);

        Assert.True(WinFspPrerequisite.ShouldPrompt(new(WinFspStatus.Missing, null), WinFspPrerequisite.SkipKey(older)));
        Assert.True(WinFspPrerequisite.ShouldPrompt(olderCheck, null));
        Assert.False(WinFspPrerequisite.ShouldPrompt(olderCheck, WinFspPrerequisite.SkipKey(older)));
        Assert.True(WinFspPrerequisite.ShouldPrompt(olderCheck, WinFspPrerequisite.SkipKey(new Version(2, 1, 25156))));
        Assert.False(WinFspPrerequisite.ShouldPrompt(new(WinFspStatus.Ok, WinFspPrerequisite.RequiredVersion), null));
        Assert.False(WinFspPrerequisite.ShouldPrompt(new(WinFspStatus.Newer, new Version(2, 3, 1)), null));
    }
}
