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
    /// Tries to connect to a running instance's CLI pipe and execute <paramref name="args"/>
    /// there.
    /// </summary>
    /// <returns>
    /// <c>true</c> if a running instance answered the request (regardless of the command's own
    /// exit code); <c>false</c> if no instance is currently listening on the pipe.
    /// </returns>
    public static bool TrySend(string[] args, out string output, out int exitCode)
    {
        output = string.Empty;
        exitCode = 1;

        using var pipe = new NamedPipeClientStream(".", CliPipeProtocol.PipeName, PipeDirection.InOut);

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

        var responseJson = reader.ReadLine();
        if (responseJson == null)
        {
            return false;
        }

        var response = CliPipeProtocol.DeserializeResponse(responseJson);
        output = response.Output;
        exitCode = response.ExitCode;
        return true;
    }
}