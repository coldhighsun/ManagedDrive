using YamlDotNet.Serialization;

namespace ManagedDrive.WingetExtension;

internal sealed record InstallerInfo(
    string InstallerType,
    string SilentSwitches,
    string SilentWithProgressSwitches,
    string? PackageIdentifier = null);

// `winget show`'s text output is localized to the OS UI language, so field names can't be
// pattern-matched reliably. `winget download` instead writes a companion manifest YAML next to
// the installer with stable, locale-independent field names — parse that instead.
//
// Deserializes into plain Dictionary<object,object>/List<object>/string rather than typed POCOs:
// YamlDotNet's typed deserialization constructs app-defined types via reflection
// (Activator.CreateInstance), which a trimmed publish would break by removing their otherwise-
// unreferenced parameterless constructors (MissingMethodException at runtime). This project isn't
// currently published trimmed (PublishTrimmed requires a self-contained publish; this ships
// framework-dependent, like the rest of the app), but Dictionary/List/string are BCL types the
// trimmer always preserves regardless, so this stays safe if that ever changes.
internal static class WingetManifestReader
{
    private static readonly string[] MsiInstallerTypes = ["msi", "wix"];
    private static readonly string[] ExeInstallerTypes = ["exe", "inno", "nullsoft", "burn"];

    public static bool IsMsiBased(string installerType) =>
        MsiInstallerTypes.Contains(installerType, StringComparer.OrdinalIgnoreCase);

    public static bool IsExeBased(string installerType) =>
        ExeInstallerTypes.Contains(installerType, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the installer type, silent switches and package id from a downloaded manifest.
    /// </summary>
    /// <param name="manifestYamlPath">The manifest file.</param>
    /// <param name="installerFileName">
    /// The downloaded installer's file name, used with <paramref name="architecture"/> to pick the
    /// entry that describes it when the manifest lists several installers; without them the first
    /// entry is used.
    /// </param>
    /// <param name="architecture">The architecture the user requested, if any.</param>
    /// <returns>The installer info.</returns>
    public static InstallerInfo ReadInstallerInfo(
        string manifestYamlPath, string? installerFileName = null, string? architecture = null)
    {
        var deserializer = new DeserializerBuilder().Build();

        using var reader = new StreamReader(manifestYamlPath);
        var root = deserializer.Deserialize<Dictionary<object, object>>(reader)
            ?? throw new InvalidOperationException($"Manifest at '{manifestYamlPath}' could not be parsed.");

        var candidates = (GetValue(root, "Installers") as List<object>)?.OfType<Dictionary<object, object>>().ToList();
        if (candidates is not { Count: > 0 })
        {
            throw new InvalidOperationException($"Manifest at '{manifestYamlPath}' declares no installers.");
        }

        var installer = SelectInstaller(candidates, root, installerFileName, architecture);

        // A manifest may state the installer type and switches once at the root; each installer
        // entry then inherits them unless it overrides them.
        var installerType = InstallerTypeOf(installer, root)
            ?? throw new InvalidOperationException($"Manifest at '{manifestYamlPath}' installer has no InstallerType.");

        var switches = (GetValue(installer, "InstallerSwitches") ?? GetValue(root, "InstallerSwitches")) as Dictionary<object, object>;
        var silentSwitches = switches is not null && GetValue(switches, "Silent") is string silent
            ? silent
            : DefaultSilentSwitches(installerType);
        var silentWithProgressSwitches = switches is not null && GetValue(switches, "SilentWithProgress") is string silentWithProgress
            ? silentWithProgress
            : DefaultSilentWithProgressSwitches(installerType);

        return new(installerType, silentSwitches, silentWithProgressSwitches, GetValue(root, "PackageIdentifier") as string);
    }

    /// <summary>
    /// Picks the installer entry for the downloaded file. The architecture narrows the entries
    /// when the user asked for one; the file extension then keeps an msi-typed entry from being
    /// paired with an exe (or the reverse).
    /// </summary>
    /// <param name="candidates">The manifest's installer entries.</param>
    /// <param name="root">The manifest root, which entries inherit from.</param>
    /// <param name="installerFileName">The downloaded installer's file name, if known.</param>
    /// <param name="architecture">The requested architecture, if any.</param>
    /// <returns>The entry to use.</returns>
    /// <exception cref="NotSupportedException">
    /// Several candidates remain and none matches the file, so the choice would be a guess and is
    /// left to plain winget.
    /// </exception>
    private static Dictionary<object, object> SelectInstaller(
        List<Dictionary<object, object>> candidates, Dictionary<object, object> root, string? installerFileName, string? architecture)
    {
        var pool = candidates;
        if (!string.IsNullOrEmpty(architecture))
        {
            var byArchitecture = pool
                .Where(c => string.Equals(GetValue(c, "Architecture") as string, architecture, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byArchitecture.Count > 0)
            {
                pool = byArchitecture;
            }
        }

        var extension = string.IsNullOrEmpty(installerFileName) ? null : Path.GetExtension(installerFileName);
        if (extension is not null)
        {
            var byType = pool.Where(c => TypeMatchesExtension(InstallerTypeOf(c, root), extension)).ToList();
            if (byType.Count > 0)
            {
                return byType[0];
            }

            if (pool.Count > 1)
            {
                throw new NotSupportedException(
                    $"The manifest lists {pool.Count} installers and none matches the downloaded '{extension}' file.");
            }
        }

        return pool[0];
    }

    /// <summary>
    /// Gets an installer entry's type, falling back to the manifest root's.
    /// </summary>
    /// <param name="installer">The installer entry.</param>
    /// <param name="root">The manifest root.</param>
    /// <returns>The installer type, or <c>null</c> if neither states one.</returns>
    private static string? InstallerTypeOf(Dictionary<object, object> installer, Dictionary<object, object> root) =>
        (GetValue(installer, "InstallerType") ?? GetValue(root, "InstallerType")) as string;

    /// <summary>
    /// Whether an installer type is one that ships as a file with the given extension.
    /// </summary>
    /// <param name="installerType">The manifest installer type.</param>
    /// <param name="extension">The downloaded file's extension, with the dot.</param>
    /// <returns><c>true</c> if the type matches the extension.</returns>
    private static bool TypeMatchesExtension(string? installerType, string extension) =>
        installerType is not null && extension.ToLowerInvariant() switch
        {
            ".msi" => IsMsiBased(installerType),
            ".exe" => IsExeBased(installerType),
            _ => false,
        };

    private static object? GetValue(Dictionary<object, object> map, string key) =>
        map.TryGetValue(key, out var value) ? value : null;

    // winget's documented built-in default switches, used when the manifest doesn't override them.
    private static string DefaultSilentSwitches(string installerType) => installerType.ToLowerInvariant() switch
    {
        "msi" or "wix" => "/quiet REBOOT=ReallySuppress",
        "inno" => "/VERYSILENT /NORESTART",
        "nullsoft" => "/S",
        "burn" => "/quiet",
        _ => throw new NotSupportedException(
            $"No known default silent switches for installer type '{installerType}'; manifest must specify InstallerSwitches.Silent."),
    };

    // winget's documented built-in "silent with progress" switches: unlike full Silent, these show
    // a progress UI (but require no user interaction), used when the manifest doesn't override them.
    private static string DefaultSilentWithProgressSwitches(string installerType) => installerType.ToLowerInvariant() switch
    {
        "msi" or "wix" => "/passive REBOOT=ReallySuppress",
        "inno" => "/SILENT /NORESTART",
        "nullsoft" => "/S",
        "burn" => "/passive",
        _ => throw new NotSupportedException(
            $"No known default silent-with-progress switches for installer type '{installerType}'; manifest must specify InstallerSwitches.SilentWithProgress."),
    };
}
