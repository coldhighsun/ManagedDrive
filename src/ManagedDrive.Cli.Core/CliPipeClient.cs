using System.IO.Pipes;

namespace ManagedDrive.Cli.Core;

/// <summary>
/// Attempts to forward CLI arguments to an already-running ManagedDrive tray instance via the
/// named pipe hosted by the app layer's CLI pipe server.
/// </summary>
public static class CliPipeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Upper bound on waiting for the running instance's response line. This is a deadlock guard,
    /// not a throttle on legitimately slow commands (e.g. exporting a large disk) — the server
    /// dispatches the command onto the UI thread and awaits it there before writing back, so a
    /// generous ceiling avoids cutting off a real (if slow) response while still bounding how long
    /// a wedged instance can hang the CLI. Overridable by tests via
    /// <see cref="TestReadTimeoutOverride"/> to exercise the timeout path without a real 5-minute
    /// wait.
    /// </summary>
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Test-only override for <see cref="DefaultReadTimeout"/>; <see langword="null"/> means use
    /// the production default. Set via <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static TimeSpan? TestReadTimeoutOverride;

    private static TimeSpan ReadTimeout => TestReadTimeoutOverride ?? DefaultReadTimeout;

    /// <summary>
    /// Test-only override for the pipe name <see cref="TrySend"/> connects to; <see langword="null"/>
    /// means use <see cref="CliPipeProtocol.PipeName"/>. Lets tests stand up a fake server without
    /// colliding with a real running instance's pipe. Set via
    /// <c>InternalsVisibleTo("ManagedDrive.Tests")</c>.
    /// </summary>
    internal static string? TestPipeNameOverride;

    private static string PipeName => TestPipeNameOverride ?? CliPipeProtocol.PipeName;

    /// <summary>
    /// Tries to connect to a running instance's CLI pipe and execute <paramref name="args"/>
    /// there.
    /// </summary>
    /// <returns>
    /// <c>true</c> if a running instance answered the request (regardless of the command's own
    /// exit code); <c>false</c> if no instance is currently listening on the pipe.
    /// </returns>
    public static bool TrySend(string[] args, out CliResponse response)
    {
        response = new(false, string.Empty, null, 1);

        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            pipe.Connect(ConnectTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return false;
        }

        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true);
        writer.AutoFlush = true;

        writer.WriteLine(CliPipeProtocol.SerializeRequest(args));

        // Deliberately not `new CancellationTokenSource(ReadTimeout)`: that schedules its Cancel()
        // call on the ThreadPool, whose timer callback can be delayed well past ReadTimeout if the
        // pool is briefly starved (e.g. many parallel tests each blocked in a sync-over-async call
        // like this one). Task.WaitAny blocks this thread with a real kernel-level timeout instead,
        // so the deadline is enforced even under ThreadPool contention.
        using var readCts = new CancellationTokenSource();
        var readTask = reader.ReadLineAsync(readCts.Token).AsTask();

        if (Task.WaitAny([readTask], ReadTimeout) == -1)
        {
            readCts.Cancel();
            return false;
        }

        var responseJson = readTask.GetAwaiter().GetResult();

        if (responseJson == null)
        {
            return false;
        }

        response = CliPipeProtocol.DeserializeResponse(responseJson);
        return true;
    }
}
