using System.Text.Json.Nodes;

namespace ManagedDrive.App.Services;

/// <summary>
/// Brings the compression fields of a parsed <c>settings.json</c> up to date before it is
/// deserialized: versions that still had the Fastest/Optimal/SmallestSize presets stored
/// <c>CompressionLevel</c> (0-3) plus an optional <c>CustomZstdLevel</c> per disk and
/// <c>DefaultCompressionLevel</c> at the top level; they are now <c>ZstdLevel</c> and
/// <c>DefaultZstdLevel</c>, plain Zstd numbers. A number that is not a valid level
/// (see <see cref="ZstdLevels.IsValid"/>) becomes <see cref="ZstdLevels.Default"/>.
/// </summary>
internal static class CompressionSettingsMigration
{
    /// <summary>
    /// JSON key of a disk profile's level.
    /// </summary>
    private const string DiskKey = "ZstdLevel";

    /// <summary>
    /// JSON key of the default level for new disks.
    /// </summary>
    private const string DefaultKey = "DefaultZstdLevel";

    /// <summary>
    /// Converts and validates the compression fields of the configuration object in place.
    /// </summary>
    /// <param name="root">The parsed settings document; anything but an object is left alone.</param>
    public static void Apply(JsonNode? root)
    {
        if (root is not JsonObject config)
        {
            return;
        }

        Migrate(config, "DefaultCompressionLevel", customKey: null, DefaultKey);

        if (config["Disks"] is JsonArray disks)
        {
            foreach (var disk in disks.OfType<JsonObject>())
            {
                Migrate(disk, "CompressionLevel", "CustomZstdLevel", DiskKey);
            }
        }
    }

    /// <summary>
    /// Migrates one object: old keys become the new key unless the new key is already there, the
    /// old keys are removed, and the resulting value is validated.
    /// </summary>
    /// <param name="obj">The object holding the fields.</param>
    /// <param name="legacyKey">The old preset key (0-3).</param>
    /// <param name="customKey">The old custom Zstd level key, or <c>null</c> when there is none.</param>
    /// <param name="newKey">The new level key.</param>
    private static void Migrate(JsonObject obj, string legacyKey, string? customKey, string newKey)
    {
        var hasLegacy = obj.ContainsKey(legacyKey);
        var hasCustom = customKey is not null && obj.ContainsKey(customKey);

        if (!obj.ContainsKey(newKey) && (hasLegacy || hasCustom))
        {
            // Without a preset key the old default (Fastest) applied; the custom level wins either way.
            var preset = hasLegacy ? ReadInt(obj[legacyKey]) : 1;
            var custom = hasCustom ? ReadInt(obj[customKey!]) : null;
            obj[newKey] = preset is { } p
                ? ZstdLevels.FromLegacy(p, custom)
                : ZstdLevels.Default;
        }

        obj.Remove(legacyKey);
        if (customKey is not null)
        {
            obj.Remove(customKey);
        }

        if (obj[newKey] is { } value && (ReadInt(value) is not { } level || !ZstdLevels.IsValid(level)))
        {
            obj[newKey] = ZstdLevels.Default;
        }
    }

    /// <summary>
    /// Reads a JSON node as a whole number.
    /// </summary>
    /// <param name="node">The node to read.</param>
    /// <returns>The number, or <c>null</c> when the node is not an integer.</returns>
    private static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
}
