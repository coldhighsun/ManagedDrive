namespace ManagedDrive.Core.Persistence;

/// <summary>
/// How many worker threads an image save may keep busy, and at which priority. A save started by
/// the user while the UI is up (<see cref="BeginInteractive"/>) stops at half of the logical cores
/// and runs its workers at below-normal priority: using every core for tens of seconds starves the
/// rest of the process, notably WPF's render thread (a render pass then takes seconds). Every other
/// save (the exit save, the save when Windows logs off) keeps all cores at normal priority, since
/// nothing needs the spare cores there and those saves run against a deadline. The mode is ambient
/// (<see cref="AsyncLocal{T}"/>), so it follows the call chain into <see cref="Task.Run(Action)"/>
/// and <see cref="Parallel"/> workers without a parameter on every method in between.
/// </summary>
internal static class SaveParallelism
{
    /// <summary>
    /// Whether the saves started on the current logical call chain are interactive.
    /// </summary>
    private static readonly AsyncLocal<bool> InteractiveFlag = new();

    /// <summary>
    /// Gets whether the current call chain is inside <see cref="BeginInteractive"/>.
    /// </summary>
    public static bool IsInteractive => InteractiveFlag.Value;

    /// <summary>
    /// Gets the most worker threads a save on the current call chain should use at once: half of
    /// the logical cores (at least 2) when interactive, all of them otherwise.
    /// </summary>
    public static int Max => IsInteractive ? InteractiveMax : Environment.ProcessorCount;

    /// <summary>
    /// Gets the worker limit of an interactive save: half of the logical cores, at least 2. On a
    /// machine with simultaneous multithreading that is about one thread per physical core.
    /// </summary>
    public static int InteractiveMax { get; } = Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>
    /// Gets loop options that cap a <see cref="Parallel"/> loop at <see cref="Max"/>. A new
    /// instance every time, so no loop can change another's limits.
    /// </summary>
    public static ParallelOptions Options => new() { MaxDegreeOfParallelism = Max };

    /// <summary>
    /// Marks the saves started on the current call chain as interactive until the returned scope is
    /// disposed, which puts the previous mode back.
    /// </summary>
    /// <returns>The scope to dispose when the interactive save ends.</returns>
    public static IDisposable BeginInteractive()
    {
        var previous = InteractiveFlag.Value;
        InteractiveFlag.Value = true;
        return new Restore(previous);
    }

    /// <summary>
    /// Lowers the calling worker thread's priority when the current call chain is interactive, and
    /// does nothing otherwise.
    /// </summary>
    /// <returns>A scope whose disposal restores the previous priority.</returns>
    public static BackgroundPriorityScope EnterWorker() => BackgroundPriorityScope.EnterIf(IsInteractive);

    /// <summary>
    /// Puts the interactive flag back to what it was before <see cref="BeginInteractive"/>.
    /// </summary>
    /// <param name="previous">The value to restore.</param>
    private sealed class Restore(bool previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => InteractiveFlag.Value = previous;
    }
}
