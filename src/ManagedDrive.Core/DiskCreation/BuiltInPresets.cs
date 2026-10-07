namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// The presets that ship with the app.
/// </summary>
public static class BuiltInPresets
{
    /// <summary>
    /// One gibibyte, the unit the suggested capacities are written in.
    /// </summary>
    private const ulong Gib = 1024UL * 1024 * 1024;

    /// <summary>
    /// Gets a disk for the user's temp directory.
    /// </summary>
    public static DiskPreset Temp { get; } = new()
    {
        Id = "temp",
        CapacityBytes = 4 * Gib,
        VolumeLabel = "Temp",
        Folders = ["Temp"],
        EnvRedirects =
        [
            new() { Variable = "TEMP", SubPath = "Temp" },
            new() { Variable = "TMP", SubPath = "Temp" },
        ],
    };

    /// <summary>
    /// Gets a disk for the npm, Yarn and pnpm caches.
    /// </summary>
    public static DiskPreset Node { get; } = new()
    {
        Id = "node",
        CapacityBytes = 2 * Gib,
        VolumeLabel = "Node Cache",
        Folders = ["npm-cache", "yarn-cache", "pnpm-store"],
        EnvRedirects =
        [
            new() { Variable = "npm_config_cache", SubPath = "npm-cache" },
            new() { Variable = "YARN_CACHE_FOLDER", SubPath = "yarn-cache" },
            new() { Variable = "npm_config_store_dir", SubPath = "pnpm-store" },
        ],
    };

    /// <summary>
    /// Gets a disk for the NuGet package cache.
    /// </summary>
    public static DiskPreset NuGet { get; } = new()
    {
        Id = "nuget",
        CapacityBytes = 2 * Gib,
        VolumeLabel = "NuGet Cache",
        Folders = ["nuget"],
        EnvRedirects = [new() { Variable = "NUGET_PACKAGES", SubPath = "nuget" }],
    };

    /// <summary>
    /// Gets a disk for the pip cache.
    /// </summary>
    public static DiskPreset Python { get; } = new()
    {
        Id = "python",
        CapacityBytes = 1 * Gib,
        VolumeLabel = "Python Cache",
        Folders = ["pip-cache"],
        EnvRedirects = [new() { Variable = "PIP_CACHE_DIR", SubPath = "pip-cache" }],
    };

    /// <summary>
    /// Gets a disk for a browser cache. Browsers have no environment variable for this: the
    /// folder is created, and the browser is started with <c>--disk-cache-dir</c>.
    /// </summary>
    public static DiskPreset Browser { get; } = new()
    {
        Id = "browser",
        CapacityBytes = 1 * Gib,
        VolumeLabel = "Browser Cache",
        Folders = ["BrowserCache"],
    };

    /// <summary>
    /// Gets every built-in preset, in display order.
    /// </summary>
    public static IReadOnlyList<DiskPreset> All { get; } = [Temp, Node, NuGet, Python, Browser];
}
