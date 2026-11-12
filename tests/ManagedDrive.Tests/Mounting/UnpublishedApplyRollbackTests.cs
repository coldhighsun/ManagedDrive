namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="UnpublishedApplyRollback.Undo"/>: what an apply that failed before
/// publishing its options takes back.
/// </summary>
public sealed class UnpublishedApplyRollbackTests
{
    /// <summary>
    /// Nothing changed yet: nothing is taken back and the disk is not marked dirty.
    /// </summary>
    [Fact]
    public void Undo_NothingChanged_DoesNothing()
    {
        var steps = new List<string>();

        Run(steps, labelUpdated: false, capacityChanged: false, restoreError: null);

        Assert.Empty(steps);
    }

    /// <summary>
    /// Only the label changed: it is put back, and the disk is not marked dirty for it.
    /// </summary>
    [Fact]
    public void Undo_OnlyLabelChanged_RestoresLabel()
    {
        var steps = new List<string>();

        Run(steps, labelUpdated: true, capacityChanged: false, restoreError: null);

        Assert.Equal(["label"], steps);
    }

    /// <summary>
    /// Label and capacity changed: both are put back, then the disk is marked dirty.
    /// </summary>
    [Fact]
    public void Undo_LabelAndCapacityChanged_RestoresBothThenMarksDirty()
    {
        var steps = new List<string>();

        Run(steps, labelUpdated: true, capacityChanged: true, restoreError: null);

        Assert.Equal(["label", "capacity", "dirty"], steps);
    }

    /// <summary>
    /// A capacity that cannot be taken back is reported, and the disk is still marked dirty.
    /// </summary>
    [Fact]
    public void Undo_CapacityRestoreRefused_ReportsAndStillMarksDirty()
    {
        var steps = new List<string>();

        Run(steps, labelUpdated: false, capacityChanged: true, restoreError: "usage too high");

        Assert.Equal(["capacity", "kept:usage too high", "dirty"], steps);
    }

    /// <summary>
    /// Runs the rollback, recording each step in <paramref name="steps"/>.
    /// </summary>
    /// <param name="steps">Receives the steps in the order they ran.</param>
    /// <param name="labelUpdated">Whether the label was changed.</param>
    /// <param name="capacityChanged">Whether the capacity was changed.</param>
    /// <param name="restoreError">The reason restoring the capacity fails, or <c>null</c> when it works.</param>
    private static void Run(List<string> steps, bool labelUpdated, bool capacityChanged, string? restoreError) =>
        UnpublishedApplyRollback.Undo(
            labelUpdated,
            capacityChanged,
            () => steps.Add("label"),
            () =>
            {
                steps.Add("capacity");
                return restoreError;
            },
            error => steps.Add($"kept:{error}"),
            () => steps.Add("dirty"));
}
