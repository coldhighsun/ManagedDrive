using ManagedDrive.Service;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the input validation patterns of <see cref="GlobalMountManager"/>, which guard the
/// SYSTEM service's ability to create global symlinks.
/// </summary>
public sealed class GlobalMountManagerValidationTests
{
    /// <summary>
    /// Only a bare drive letter and colon passes, not one followed by a line break (which
    /// <c>$</c> would have accepted).
    /// </summary>
    /// <param name="letter">The candidate drive letter.</param>
    /// <param name="expected">Whether it is accepted.</param>
    [Theory]
    [InlineData("X:", true)]
    [InlineData("z:", true)]
    [InlineData("X:\n", false)]
    [InlineData("X:\r\n", false)]
    [InlineData("X", false)]
    [InlineData("XY:", false)]
    public void DriveLetterRegex_VariousInputs_AcceptsOnlyLetterAndColon(string letter, bool expected)
    {
        Assert.Equal(expected, GlobalMountManager.DriveLetterRegex().IsMatch(letter));
    }

    /// <summary>
    /// Only a well-formed WinFsp volume device path passes, not one with a trailing line break.
    /// </summary>
    /// <param name="devicePath">The candidate device path.</param>
    /// <param name="expected">Whether it is accepted.</param>
    [Theory]
    [InlineData(@"\Device\Volume{1a2b3c4d-0000-1111-2222-333344445555}", true)]
    [InlineData("\\Device\\Volume{1a2b3c4d-0000-1111-2222-333344445555}\n", false)]
    [InlineData(@"\Device\HarddiskVolume3", false)]
    [InlineData(@"\Device\Volume{xyz}", false)]
    public void DevicePathRegex_VariousInputs_AcceptsOnlyVolumeDevicePaths(string devicePath, bool expected)
    {
        Assert.Equal(expected, GlobalMountManager.DevicePathRegex().IsMatch(devicePath));
    }
}
