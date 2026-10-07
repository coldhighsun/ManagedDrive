using System.Text.Json;

namespace ManagedDrive.Cli.Core;

/// <summary>
/// Turns a <see cref="CliOutcome"/> into the JSON text printed by commands run with the global
/// <c>--json</c> option. Property names are left as declared (PascalCase), matching what
/// <c>list --json</c> has always printed, so scripts see one naming style across commands.
/// </summary>
internal static class CliJson
{
    /// <summary>
    /// Indented output for the one-shot command results.
    /// </summary>
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>
    /// Single-line output for stream events, one JSON document per line.
    /// </summary>
    private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };

    /// <summary>
    /// The JSON shape of a command that has no data to return, only an outcome.
    /// </summary>
    /// <param name="Success">Whether the command succeeded.</param>
    /// <param name="Message">The human-readable message the command would otherwise print.</param>
    private sealed record Result(bool Success, string Message);

    /// <summary>
    /// Re-expresses <paramref name="outcome"/> as JSON: its <see cref="CliOutcome.Data"/> when the
    /// command succeeded and returned some, otherwise <c>{ "Success", "Message" }</c>. The exit
    /// code and success flag are unchanged.
    /// </summary>
    /// <param name="outcome">The outcome produced by a command handler.</param>
    /// <returns>
    /// An outcome whose <see cref="CliOutcome.Message"/> is the JSON text and whose
    /// <see cref="CliOutcome.Json"/> is set; <paramref name="outcome"/> itself when it is already
    /// JSON (<c>list</c> sets <see cref="CliOutcome.Json"/> itself and keeps its structured
    /// <see cref="CliOutcome.Disks"/>).
    /// </returns>
    internal static CliOutcome ToJsonOutcome(CliOutcome outcome)
    {
        if (outcome.Json)
        {
            return outcome;
        }

        var payload = outcome.Success && outcome.Data is not null
            ? outcome.Data
            : new Result(outcome.Success, outcome.Message);

        return outcome with
        {
            Message = Serialize(payload),
            Disks = null,
            Snapshots = null,
            Data = null,
            Json = true,
        };
    }

    /// <summary>
    /// Serializes <paramref name="value"/> as indented JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <returns>The JSON text.</returns>
    internal static string Serialize(object value) => JsonSerializer.Serialize(value, IndentedOptions);

    /// <summary>
    /// Serializes <paramref name="value"/> as a single line of JSON, for newline-delimited streams.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <returns>The JSON text, without a trailing newline.</returns>
    internal static string SerializeLine(object value) => JsonSerializer.Serialize(value, CompactOptions);
}
