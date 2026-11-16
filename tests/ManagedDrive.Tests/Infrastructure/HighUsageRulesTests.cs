using ManagedDrive.App.Infrastructure;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="HighUsageRules"/>: the warning state follows usage exactly, only the one-shot warning is re-armed
/// with a gap.
/// </summary>
public sealed class HighUsageRulesTests
{
    /// <summary>
    /// Verifies that crossing the threshold turns the warning on and raises the one-shot warning once.
    /// </summary>
    [Fact]
    public void Evaluate_RisesToThreshold_IsHighAndRaisesWarning()
    {
        var state = HighUsageRules.Evaluate(90, threshold: 90, wasArmed: true);

        Assert.True(state.IsHigh);
        Assert.True(state.RaiseWarning);
        Assert.False(state.IsArmed);
    }

    /// <summary>
    /// Verifies that staying above the threshold does not raise the warning again.
    /// </summary>
    [Fact]
    public void Evaluate_StaysAboveThreshold_DoesNotRaiseAgain()
    {
        var state = HighUsageRules.Evaluate(95, threshold: 90, wasArmed: false);

        Assert.True(state.IsHigh);
        Assert.False(state.RaiseWarning);
    }

    /// <summary>
    /// Verifies that the warning state clears as soon as usage is back under the threshold, even within the re-arm gap.
    /// </summary>
    [Fact]
    public void Evaluate_DropsJustBelowThreshold_ClearsWarningStateButStaysDisarmed()
    {
        var state = HighUsageRules.Evaluate(88, threshold: 90, wasArmed: false);

        Assert.False(state.IsHigh);
        Assert.False(state.IsArmed);
        Assert.False(state.RaiseWarning);
    }

    /// <summary>
    /// Verifies that falling more than the gap below the threshold re-arms the one-shot warning.
    /// </summary>
    [Fact]
    public void Evaluate_DropsBelowResetGap_Rearms()
    {
        var state = HighUsageRules.Evaluate(80, threshold: 90, wasArmed: false);

        Assert.False(state.IsHigh);
        Assert.True(state.IsArmed);
    }

    /// <summary>
    /// Verifies that a rise right after a drop inside the gap turns the warning state on without a new one-shot warning.
    /// </summary>
    [Fact]
    public void Evaluate_RisesAgainWithinGap_IsHighWithoutRaisingWarning()
    {
        var state = HighUsageRules.Evaluate(91, threshold: 90, wasArmed: false);

        Assert.True(state.IsHigh);
        Assert.False(state.RaiseWarning);
    }

    /// <summary>
    /// Verifies that a disk without a warning threshold is never high and stays armed.
    /// </summary>
    [Fact]
    public void Evaluate_NoThreshold_IsNeverHigh()
    {
        var state = HighUsageRules.Evaluate(99, threshold: null, wasArmed: false);

        Assert.False(state.IsHigh);
        Assert.True(state.IsArmed);
        Assert.False(state.RaiseWarning);
    }
}
