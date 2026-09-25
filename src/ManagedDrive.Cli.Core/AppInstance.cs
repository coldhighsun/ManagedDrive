namespace ManagedDrive.Cli.Core;

/// <summary>
/// Identifies the single running ManagedDrive app instance. The app holds the
/// <see cref="SingleInstanceMutexName"/> mutex for its lifetime; <c>mdrive</c> checks it before
/// launching the app, since a running instance can still fail to accept a CLI connection in time
/// (its pipe serves one connection at a time, and a long command keeps it busy).
/// </summary>
public static class AppInstance
{
    /// <summary>
    /// Name of the mutex the running app instance holds to enforce a single instance per machine.
    /// </summary>
    public const string SingleInstanceMutexName = "Global\\ManagedDrive-4A7C2E1B-9F3D-4B8A-A1C5-3E6D2F0B8C9A";

    /// <summary>
    /// Test-only override for the mutex name <see cref="IsRunning"/> checks; <see langword="null"/>
    /// means use <see cref="SingleInstanceMutexName"/>. Set via
    /// <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static string? TestMutexNameOverride;

    /// <summary>
    /// Gets the mutex name <see cref="IsRunning"/> checks.
    /// </summary>
    private static string MutexName => TestMutexNameOverride ?? SingleInstanceMutexName;

    /// <summary>
    /// Checks whether an app instance is currently running, i.e. whether its single-instance
    /// mutex exists.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the mutex exists — including when it exists but this process may
    /// not open it (an instance running elevated or as another user); otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool IsRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out var mutex))
            {
                return false;
            }

            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
