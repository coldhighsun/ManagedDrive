namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="SaveCapacityFix"/>: what a save does when the capacity grew while it ran.
/// </summary>
public sealed class SaveCapacityFixTests
{
    /// <summary>
    /// An unchanged or lowered capacity needs no patch: the header is fine and nothing is touched.
    /// </summary>
    /// <param name="currentCapacity">The capacity after the save.</param>
    [Theory]
    [InlineData(1024ul)]
    [InlineData(512ul)]
    public void TryFixHeader_CapacityNotRaised_ReturnsTrueWithoutPatching(ulong currentCapacity)
    {
        var patched = false;
        var dirty = false;

        var fine = SaveCapacityFix.TryFixHeader(1024, currentCapacity, _ => patched = true, () => dirty = true);

        Assert.True(fine);
        Assert.False(patched);
        Assert.False(dirty);
    }

    /// <summary>
    /// A raised capacity is patched into the header, with the new value, and the disk stays clean.
    /// </summary>
    [Fact]
    public void TryFixHeader_CapacityRaisedAndPatched_ReturnsTrue()
    {
        ulong? patchedTo = null;
        var dirty = false;

        var fine = SaveCapacityFix.TryFixHeader(1024, 4096, capacity =>
        {
            patchedTo = capacity;
            return true;
        }, () => dirty = true);

        Assert.True(fine);
        Assert.Equal(4096UL, patchedTo);
        Assert.False(dirty);
    }

    /// <summary>
    /// A raised capacity that cannot be patched marks the disk dirty and asks for another save.
    /// </summary>
    [Fact]
    public void TryFixHeader_CapacityRaisedPatchRefused_MarksDirtyAndReturnsFalse()
    {
        var dirty = false;

        var fine = SaveCapacityFix.TryFixHeader(1024, 4096, _ => false, () => dirty = true);

        Assert.False(fine);
        Assert.True(dirty);
    }

    /// <summary>
    /// A pass whose header matches is the only one, and nothing is reported.
    /// </summary>
    [Fact]
    public void SaveUntilHeaderMatches_FirstPassMatches_RunsOnce()
    {
        var repeats = 0;

        var passes = SaveCapacityFix.SaveUntilHeaderMatches(() => true, () => repeats++);

        Assert.Equal(1, passes);
        Assert.Equal(0, repeats);
    }

    /// <summary>
    /// Passes are repeated until one matches, reporting each repeat.
    /// </summary>
    [Fact]
    public void SaveUntilHeaderMatches_TwoPassesNeedRepeat_RunsUntilMatch()
    {
        var results = new Queue<bool>([false, false, true]);
        var repeats = 0;

        var passes = SaveCapacityFix.SaveUntilHeaderMatches(results.Dequeue, () => repeats++);

        Assert.Equal(3, passes);
        Assert.Equal(2, repeats);
    }

    /// <summary>
    /// A pass that throws stops the loop and the exception goes on.
    /// </summary>
    [Fact]
    public void SaveUntilHeaderMatches_PassThrows_Propagates()
    {
        Assert.Throws<IOException>(() =>
            SaveCapacityFix.SaveUntilHeaderMatches(() => throw new IOException("disk full"), () => { }));
    }
}
