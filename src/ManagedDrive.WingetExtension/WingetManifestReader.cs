using YamlDotNet.Serialization;

namespace ManagedDrive.WingetExtension;

internal sealed record InstallerInfo(string InstallerType, string SilentSwitches, string SilentWithProgressSwitches);

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

    public static InstallerInfo ReadInstallerInfo(string manifestYamlPath)
    {
        var deserializer = new DeserializerBuilder().Build();

        using var reader = new StreamReader(manifestYamlPath);
        var root = deserializer.Deserialize<Dictionary<object, object>>(reader)
            ?? throw new InvalidOperationException($"Manifest at '{manifestYamlPath}' could not be parsed.");

        var installers = GetValue(root, "Installers") as List<object>
            ?? throw new InvalidOperationException($"Manifest at '{manifestYamlPath}' declares no installers.");

        var installer = installers.FirstOrDefault() as Dictionary<object, object>
            ?? throw new InvalidOperationException($"Manifest at '{manifestYamlPath}' declares no installers.");

        var installerType = GetValue(installer, "InstallerType") as string
            ?? throw new InvalidOperationException($"Manifest at '{manifestYamlPath}' installer has no InstallerType.");

        var switches = GetValue(installer, "InstallerSwitches") as Dictionary<object, object>;
        var silentSwitches = switches is not null && GetValue(switches, "Silent") is string silent
            ? silent
            : DefaultSilentSwitches(installerType);
        var silentWithProgressSwitches = switches is not null && GetValue(switches, "SilentWithProgress") is string silentWithProgress
            ? silentWithProgress
            : DefaultSilentWithProgressSwitches(installerType);

        return new InstallerInfo(installerType, silentSwitches, silentWithProgressSwitches);
    }

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
