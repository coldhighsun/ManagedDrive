namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// The result of combining several presets on one disk.
/// </summary>
public sealed record PresetComposition
{
    /// <summary>
    /// Gets the sum of the presets' suggested capacities, in bytes.
    /// </summary>
    public ulong CapacityBytes
    {
        get; init;
    }

    /// <summary>
    /// Gets the volume label of the first preset that has one, or <c>null</c>.
    /// </summary>
    public string? VolumeLabel
    {
        get; init;
    }

    /// <summary>
    /// Gets the compression level of the first preset that has one, or <c>null</c>.
    /// </summary>
    public ImageCompressionLevel? CompressionLevel
    {
        get; init;
    }

    /// <summary>
    /// Gets the union of the presets' folders, without duplicates.
    /// </summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>
    /// Gets the union of the presets' redirections. A variable redirected to two different
    /// folders keeps the first and is listed in <see cref="Conflicts"/>.
    /// </summary>
    public IReadOnlyList<EnvRedirect> EnvRedirects { get; init; } = [];

    /// <summary>
    /// Gets the variables that two presets point at different folders.
    /// </summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];
}

/// <summary>
/// Combines presets, so one disk can host several caches.
/// </summary>
public static class PresetComposer
{
    /// <summary>
    /// Merges <paramref name="presets"/> in order.
    /// </summary>
    /// <param name="presets">The presets to combine.</param>
    /// <returns>The combined suggestion.</returns>
    public static PresetComposition Merge(IEnumerable<DiskPreset> presets)
    {
        var capacity = 0UL;
        string? label = null;
        ImageCompressionLevel? compression = null;
        var folders = new List<string>();
        var redirects = new List<EnvRedirect>();
        var conflicts = new List<string>();

        foreach (var preset in presets)
        {
            capacity = ulong.MaxValue - capacity < preset.CapacityBytes ? ulong.MaxValue : capacity + preset.CapacityBytes;
            label ??= preset.VolumeLabel;
            compression ??= preset.CompressionLevel;

            foreach (var folder in preset.Folders)
            {
                if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    folders.Add(folder);
                }
            }

            foreach (var redirect in preset.EnvRedirects)
            {
                var existing = redirects.Find(r => string.Equals(r.Variable, redirect.Variable, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    redirects.Add(redirect);
                }
                else if (!string.Equals(existing.SubPath.Trim('\\', '/'), redirect.SubPath.Trim('\\', '/'), StringComparison.OrdinalIgnoreCase) &&
                    !conflicts.Contains(redirect.Variable, StringComparer.OrdinalIgnoreCase))
                {
                    conflicts.Add(redirect.Variable);
                }
            }
        }

        return new()
        {
            CapacityBytes = capacity,
            VolumeLabel = label,
            CompressionLevel = compression,
            Folders = folders,
            EnvRedirects = redirects,
            Conflicts = conflicts,
        };
    }
}
