using ManagedDrive.WingetExtension;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the pure parts of <see cref="SilentInstaller"/>: argument building, exit-code
/// mapping and option extraction.
/// </summary>
public sealed class SilentInstallerHelpersTests
{
    /// <summary>
    /// Both agreement flags are added when the request has neither.
    /// </summary>
    [Fact]
    public void BuildDownloadArguments_NoAgreementFlags_AddsBoth()
    {
        var args = SilentInstaller.BuildDownloadArguments(@"C:\dl", ["--id", "Git.Git"]);

        Assert.Equal(1, args.Count(a => a == "--accept-package-agreements"));
        Assert.Equal(1, args.Count(a => a == "--accept-source-agreements"));
        Assert.Equal(["--id", "Git.Git"], args.TakeLast(2));
    }

    /// <summary>
    /// A flag the user already passed is not repeated, since winget rejects duplicates.
    /// </summary>
    [Fact]
    public void BuildDownloadArguments_UserPassedAgreementFlag_DoesNotRepeatIt()
    {
        var args = SilentInstaller.BuildDownloadArguments(
            @"C:\dl", ["--id", "Git.Git", "--accept-package-agreements", "--accept-source-agreements"]);

        Assert.Equal(1, args.Count(a => a == "--accept-package-agreements"));
        Assert.Equal(1, args.Count(a => a == "--accept-source-agreements"));
    }

    /// <summary>
    /// Restart-required codes are successes, as winget treats them; other codes pass through.
    /// </summary>
    /// <param name="installerExitCode">The installer's exit code.</param>
    /// <param name="expected">The exit code wingetx reports.</param>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3010, 0)]
    [InlineData(1641, 0)]
    [InlineData(1603, 1603)]
    [InlineData(1, 1)]
    public void MapInstallerExitCode_VariousCodes_MapsRestartRequiredToSuccess(int installerExitCode, int expected)
    {
        Assert.Equal(expected, SilentInstaller.MapInstallerExitCode(installerExitCode));
    }

    /// <summary>
    /// The probe's table (which contains the package id) means "installed" or "upgrade available";
    /// its absence means neither. install defers when the id is listed, upgrade when it is not.
    /// </summary>
    /// <param name="isUpgrade">Whether the request is an upgrade.</param>
    /// <param name="output">What the probe printed.</param>
    /// <param name="expected">Whether plain winget should handle the request.</param>
    [Theory]
    [InlineData(false, "Name  Id\nGit  Git.Git  2.55", true)]
    [InlineData(false, "No installed package found matching input criteria.", false)]
    [InlineData(true, "Name  Id  Version  Available\nGit  Git.Git  2.54  2.55", false)]
    [InlineData(true, "找不到与输入条件匹配的已安装程序包。", true)]
    [InlineData(true, "", true)]
    public void ProbeOutputMeansDefer_ListedOrNot_DecidesPerRequestKind(bool isUpgrade, string output, bool expected)
    {
        Assert.Equal(expected, SilentInstaller.ProbeOutputMeansDefer(isUpgrade, "git.git", output));
    }

    /// <summary>
    /// The package id is read from the separate and the <c>=</c> forms of <c>--id</c>.
    /// </summary>
    [Fact]
    public void ExplicitPackageId_SeparateAndEqualsForms_ReturnsTheId()
    {
        Assert.Equal("Git.Git", SilentInstaller.ExplicitPackageId(["-e", "--id", "Git.Git"]));
        Assert.Equal("Git.Git", SilentInstaller.ExplicitPackageId(["--id=Git.Git"]));
        Assert.Null(SilentInstaller.ExplicitPackageId(["git"]));
        Assert.Null(SilentInstaller.ExplicitPackageId(["--id"]));
    }

    /// <summary>
    /// An exact match is only assumed when the request asked for one.
    /// </summary>
    [Fact]
    public void IsExactRequest_WithAndWithoutExactFlag_ReflectsTheRequest()
    {
        Assert.True(SilentInstaller.IsExactRequest(["--id", "Git.Git", "-e"]));
        Assert.True(SilentInstaller.IsExactRequest(["--exact", "git"]));
        Assert.False(SilentInstaller.IsExactRequest(["--id", "git.git"]));
    }

    /// <summary>
    /// The architecture is read from both its long and short option.
    /// </summary>
    [Fact]
    public void ArchitectureOf_LongAndShortOption_ReturnsTheArchitecture()
    {
        Assert.Equal("x64", SilentInstaller.ArchitectureOf(["--architecture", "x64"]));
        Assert.Equal("arm64", SilentInstaller.ArchitectureOf(["-a", "arm64"]));
        Assert.Null(SilentInstaller.ArchitectureOf(["--id", "Git.Git"]));
    }
}
