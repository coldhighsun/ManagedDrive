using ManagedDrive.Service;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for how the helper service decodes its settings.
/// </summary>
public sealed class HelperPipeServiceTests
{
    /// <summary>
    /// A nonzero DWORD turns a setting on.
    /// </summary>
    [Fact]
    public void IsEnabledSetting_NonzeroDword_ReturnsTrue()
    {
        Assert.True(HelperPipeService.IsEnabledSetting(1));
    }

    /// <summary>
    /// A zero DWORD leaves a setting off.
    /// </summary>
    [Fact]
    public void IsEnabledSetting_ZeroDword_ReturnsFalse()
    {
        Assert.False(HelperPipeService.IsEnabledSetting(0));
    }

    /// <summary>
    /// A missing value leaves a setting off.
    /// </summary>
    [Fact]
    public void IsEnabledSetting_Missing_ReturnsFalse()
    {
        Assert.False(HelperPipeService.IsEnabledSetting(null));
    }

    /// <summary>
    /// A value of another type (e.g. a string "1") is not a DWORD and leaves the setting off.
    /// </summary>
    [Fact]
    public void IsEnabledSetting_NotADword_ReturnsFalse()
    {
        Assert.False(HelperPipeService.IsEnabledSetting("1"));
    }
}
