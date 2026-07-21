namespace ManagedDrive.Tests;

/// <summary>
/// Simple synchronous <see cref="IProgress{T}"/> implementation that appends every reported
/// value to a list, for deterministic assertions in tests (unlike <see cref="Progress{T}"/>,
/// which marshals through a captured <see cref="System.Threading.SynchronizationContext"/>).
/// </summary>
internal sealed class RecordingProgress(List<double> reports) : IProgress<double>
{
    public void Report(double value) => reports.Add(value);
}