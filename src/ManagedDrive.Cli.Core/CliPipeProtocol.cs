using System.Text.Json;

namespace ManagedDrive.Cli.Core;

/// <summary>
/// Wire format shared by the app layer's CLI pipe server and <see cref="CliPipeClient"/>: a
/// single JSON line carrying a <see cref="CliRequest"/>, answered with a single JSON line
/// carrying a <see cref="CliResponse"/>.
/// </summary>
public static class CliPipeProtocol
{
    /// <summary>
    /// Named pipe used to forward CLI commands to the already-running tray instance. Shares the
    /// application's GUID with the single-instance mutex for readability, not for any
    /// functional reason.
    /// </summary>
    public const string PipeName = "ManagedDrive-CLI-4A7C2E1B-9F3D-4B8A-A1C5-3E6D2F0B8C9A";

    /// <summary>
    /// Parses a request line. Also accepts the older bare <c>string[] args</c> form (sent by
    /// clients that predate <see cref="CliRequest.WorkingDirectory"/>), which yields a request
    /// without a working directory.
    /// </summary>
    /// <param name="json">The request line.</param>
    /// <returns>The parsed request; a <c>null</c> JSON literal yields an empty request.</returns>
    public static CliRequest DeserializeRequest(string json)
    {
        if (json.TrimStart().StartsWith('['))
        {
            return new(JsonSerializer.Deserialize<string[]>(json) ?? [], null);
        }

        var request = JsonSerializer.Deserialize<CliRequest>(json);
        return new(request?.Args ?? [], request?.WorkingDirectory);
    }

    public static CliResponse DeserializeResponse(string json) =>
        JsonSerializer.Deserialize<CliResponse>(json) ?? new CliResponse(false, string.Empty, null, 1);

    /// <summary>
    /// Serializes a request line.
    /// </summary>
    /// <param name="args">The command-line arguments, without the executable name.</param>
    /// <param name="workingDirectory">The sending process's working directory.</param>
    /// <returns>The request as a single JSON line.</returns>
    public static string SerializeRequest(string[] args, string? workingDirectory) =>
        JsonSerializer.Serialize(new CliRequest(args, workingDirectory));

    public static string SerializeResponse(CliResponse response) => JsonSerializer.Serialize(response);
}

/// <summary>
/// Structured result of executing a CLI command, sent back across the pipe to the calling
/// process. Mirrors <see cref="CliOutcome"/> — rendering into terminal output is the calling
/// process's responsibility.
/// </summary>
public sealed record CliResponse(
    bool Success,
    string Message,
    IReadOnlyList<CliDiskInfo>? Disks,
    int ExitCode,
    IReadOnlyList<CliSnapshotInfo>? Snapshots = null,
    bool Json = false);

/// <summary>
/// A CLI command forwarded to the running app: the arguments plus the sender's working
/// directory, which relative file paths in <see cref="Args"/> are resolved against (see
/// <see cref="CliCommandProcessor.ExecuteAsync"/>).
/// </summary>
/// <param name="Args">The command-line arguments, without the executable name.</param>
/// <param name="WorkingDirectory">The sending process's working directory, if known.</param>
public sealed record CliRequest(string[] Args, string? WorkingDirectory);
