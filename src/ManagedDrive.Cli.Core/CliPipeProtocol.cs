using System.Text.Json;

namespace ManagedDrive.Cli.Core;

/// <summary>
/// Wire format shared by the app layer's CLI pipe server and <see cref="CliPipeClient"/>: a
/// single JSON line carrying the CLI <c>string[] args</c> request, answered with a single JSON
/// line carrying a <see cref="CliResponse"/>.
/// </summary>
public static class CliPipeProtocol
{
    /// <summary>
    /// Named pipe used to forward CLI commands to the already-running tray instance. Shares the
    /// application's GUID with the single-instance mutex for readability, not for any
    /// functional reason.
    /// </summary>
    public const string PipeName = "ManagedDrive-CLI-4A7C2E1B-9F3D-4B8A-A1C5-3E6D2F0B8C9A";

    public static string[] DeserializeRequest(string json) =>
        JsonSerializer.Deserialize<string[]>(json) ?? [];

    public static CliResponse DeserializeResponse(string json) =>
        JsonSerializer.Deserialize<CliResponse>(json) ?? new CliResponse(false, string.Empty, null, 1);

    public static string SerializeRequest(string[] args) => JsonSerializer.Serialize(args);

    public static string SerializeResponse(CliResponse response) => JsonSerializer.Serialize(response);
}

/// <summary>
/// Structured result of executing a CLI command, sent back across the pipe to the calling
/// process. Mirrors <see cref="CliOutcome"/> — rendering into terminal output is the calling
/// process's responsibility.
/// </summary>
public sealed record CliResponse(bool Success, string Message, IReadOnlyList<CliDiskInfo>? Disks, int ExitCode);