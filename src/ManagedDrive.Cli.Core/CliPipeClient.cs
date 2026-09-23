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
    /// a wedged instance can hang the CLI.
    /// </summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(5);

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

        using var pipe = new NamedPipeClientStream(".", CliPipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

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

        using var readCts = new CancellationTokenSource(ReadTimeout);
        string? responseJson;
        try
        {
            responseJson = reader.ReadLineAsync(readCts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (responseJson == null)
        {
            return false;
        }

        response = CliPipeProtocol.DeserializeResponse(responseJson);
        return true;
    }
}
