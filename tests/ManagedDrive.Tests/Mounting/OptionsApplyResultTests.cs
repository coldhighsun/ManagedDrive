namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="OptionsChange"/> and <see cref="OptionsApplyResult"/>.
/// </summary>
public sealed class OptionsApplyResultTests
{
    /// <summary>
    /// The options a change starts from.
    /// </summary>
    private static readonly DiskOptions Options = new() { MountPoint = "R:", CapacityBytes = 1024 };

    /// <summary>
    /// An edit that returned the options it was given, or equal ones, changed nothing.
    /// </summary>
    [Fact]
    public void Changed_EqualOptions_ReturnsFalse()
    {
        Assert.False(new OptionsChange(Options, Options).Changed);
        Assert.False(new OptionsChange(Options, Options with { }).Changed);
    }

    /// <summary>
    /// An edit that changed a field changed the options.
    /// </summary>
    [Fact]
    public void Changed_DifferentOptions_ReturnsTrue()
    {
        Assert.True(new OptionsChange(Options, Options with { VolumeLabel = "Edited" }).Changed);
    }

    /// <summary>
    /// A successful result carries the change and no error.
    /// </summary>
    [Fact]
    public void Succeeded_Change_IsApplied()
    {
        var change = new OptionsChange(Options, Options);

        var result = OptionsApplyResult.Succeeded(change);

        Assert.True(result.Applied);
        Assert.Same(change, result.Change);
        Assert.Null(result.Error);
    }

    /// <summary>
    /// A refused result carries the reason and no change.
    /// </summary>
    [Fact]
    public void Refused_Error_IsNotApplied()
    {
        var result = OptionsApplyResult.Refused("too small");

        Assert.False(result.Applied);
        Assert.Null(result.Change);
        Assert.Equal("too small", result.Error);
    }
}
