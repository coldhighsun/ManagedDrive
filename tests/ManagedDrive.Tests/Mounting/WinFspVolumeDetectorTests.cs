using ManagedDrive.WingetExtension;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="WinFspVolumeDetector"/>.
/// </summary>
public sealed class WinFspVolumeDetectorTests
{
    /// <summary>
    /// Only paths on a <c>\Device\Volume{GUID}</c> device (WinFsp) match; ordinary partitions and
    /// lookalike device names don't.
    /// </summary>
    /// <param name="ntPath">The NT path.</param>
    /// <param name="expected">Whether it is on a WinFsp volume.</param>
    [Theory]
    [InlineData(@"\Device\Volume{dae08b67-b880-11f1-b24d-00090ffe0001}\", true)]
    [InlineData(@"\Device\Volume{dae08b67-b880-11f1-b24d-00090ffe0001}\Temp\wingetx", true)]
    [InlineData(@"\Device\Volume{dae08b67-b880-11f1-b24d-00090ffe0001}", true)]
    [InlineData(@"\Device\HarddiskVolume3\Users\USER\AppData\Local\Temp", false)]
    [InlineData(@"\Device\Volume{dae08b67-b880-11f1-b24d-00090ffe0001}Temp", false)]
    [InlineData(@"\Device\Mup\server\share", false)]
    [InlineData("", false)]
    public void IsWinFspDevicePath_NtPath_MatchesOnlyWinFspVolumes(string ntPath, bool expected)
    {
        var isWinFsp = WinFspVolumeDetector.IsWinFspDevicePath(ntPath);

        Assert.Equal(expected, isWinFsp);
    }

    /// <summary>
    /// A directory on an ordinary partition resolves to its <c>\Device\HarddiskVolumeN</c> path
    /// and is not reported as WinFsp, including a path below it that doesn't exist yet. Uses the
    /// system directory rather than TEMP, which may itself be on a ManagedDrive RAM disk.
    /// </summary>
    [Fact]
    public void IsOnWinFspVolume_DirectoryOnOrdinaryVolume_ReturnsFalse()
    {
        var systemDirectory = Environment.SystemDirectory;

        var existing = WinFspVolumeDetector.IsOnWinFspVolume(systemDirectory);
        var missing = WinFspVolumeDetector.IsOnWinFspVolume(Path.Combine(systemDirectory, "missing", "child"));

        Assert.False(existing);
        Assert.False(missing);
    }

    /// <summary>
    /// A malformed path is reported as not WinFsp instead of throwing.
    /// </summary>
    /// <param name="path">The malformed path.</param>
    [Theory]
    [InlineData("")]
    [InlineData("C:\\bad\0path")]
    public void IsOnWinFspVolume_MalformedPath_ReturnsFalse(string path)
    {
        var isWinFsp = WinFspVolumeDetector.IsOnWinFspVolume(path);

        Assert.False(isWinFsp);
    }
}
