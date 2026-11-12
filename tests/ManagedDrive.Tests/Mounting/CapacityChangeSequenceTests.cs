namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="CapacityChangeSequence.TryApply"/>: the order in which a capacity change
/// publishes the new capacity and moves the file system's ceiling, and what a refusal undoes.
/// </summary>
public sealed class CapacityChangeSequenceTests
{
    /// <summary>
    /// Growth publishes the larger capacity before it raises the ceiling.
    /// </summary>
    [Fact]
    public void TryApply_Growth_PublishesBeforeRaisingCeiling()
    {
        var calls = new List<string>();

        var applied = CapacityChangeSequence.TryApply(
            100, 200, publish: c => calls.Add($"publish {c}"), setCeiling: c => { calls.Add($"ceiling {c}"); return true; });

        Assert.True(applied);
        Assert.Equal(["publish 200", "ceiling 200"], calls);
    }

    /// <summary>
    /// A shrink lowers the ceiling before it publishes the smaller capacity.
    /// </summary>
    [Fact]
    public void TryApply_Shrink_LowersCeilingBeforePublishing()
    {
        var calls = new List<string>();

        var applied = CapacityChangeSequence.TryApply(
            200, 100, publish: c => calls.Add($"publish {c}"), setCeiling: c => { calls.Add($"ceiling {c}"); return true; });

        Assert.True(applied);
        Assert.Equal(["ceiling 100", "publish 100"], calls);
    }

    /// <summary>
    /// A shrink the ceiling refuses (usage exceeds it) publishes nothing.
    /// </summary>
    [Fact]
    public void TryApply_ShrinkRefused_PublishesNothing()
    {
        var calls = new List<string>();

        var applied = CapacityChangeSequence.TryApply(
            200, 100, publish: c => calls.Add($"publish {c}"), setCeiling: c => { calls.Add($"ceiling {c}"); return false; });

        Assert.False(applied);
        Assert.Equal(["ceiling 100"], calls);
    }

    /// <summary>
    /// Growth the ceiling refuses takes the published capacity back to the old one.
    /// </summary>
    [Fact]
    public void TryApply_GrowthRefused_PublishesOldCapacityAgain()
    {
        var published = new List<ulong>();

        var applied = CapacityChangeSequence.TryApply(100, 200, publish: published.Add, setCeiling: _ => false);

        Assert.False(applied);
        Assert.Equal([200UL, 100UL], published);
    }

    /// <summary>
    /// Undoing a growth is a shrink back, so it follows the shrink order and leaves the published
    /// capacity alone when usage no longer fits the old capacity.
    /// </summary>
    [Fact]
    public void TryApply_UndoGrowthRefused_KeepsPublishedCapacity()
    {
        var published = new List<ulong>();

        var undone = CapacityChangeSequence.TryApply(200, 100, publish: published.Add, setCeiling: _ => false);

        Assert.False(undone);
        Assert.Empty(published);
    }
}
