using System.Diagnostics.CodeAnalysis;

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
    /// Test-only override for the mutex name <see cref="IsRunning"/> and <see cref="TryAcquire"/>
    /// use; <see langword="null"/>
    /// means use <see cref="SingleInstanceMutexName"/>. Set via
    /// <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static string? TestMutexNameOverride;

    /// <summary>
    /// Gets the mutex name <see cref="IsRunning"/> checks and <see cref="TryAcquire"/> creates.
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

    /// <summary>
    /// Tries to become the single running app instance by creating and taking ownership of its
    /// mutex.
    /// </summary>
    /// <param name="mutex">
    /// The owned mutex when this call succeeds; the caller must release and dispose it on exit.
    /// <see langword="null"/> otherwise.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if this process now owns the mutex; <see langword="false"/> if
    /// another instance already holds it — including one running elevated or as another user,
    /// whose mutex this process isn't allowed to open.
    /// </returns>
    public static bool TryAcquire([NotNullWhen(true)] out Mutex? mutex)
    {
        Mutex candidate;
        bool createdNew;
        try
        {
            candidate = new(true, MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            mutex = null;
            return false;
        }

        if (!createdNew)
        {
            candidate.Dispose();
            mutex = null;
            return false;
        }

        mutex = candidate;
        return true;
    }
}
