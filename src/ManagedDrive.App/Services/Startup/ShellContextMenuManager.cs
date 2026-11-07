namespace ManagedDrive.App.Services;

/// <summary>
/// Manages the Windows Explorer right-click context menu entry that lets a user import an
/// archive file (zip, 7z, rar, tar) directly as a read-only RAM disk. Writes to
/// <c>HKCU\Software\Classes\SystemFileAssociations\{ext}\shell\...</c> for each supported
/// extension, so no elevated privileges are required and the entry only affects the current
/// user, mirroring <see cref="StartupManager"/>'s registry access pattern.
/// </summary>
public static class ShellContextMenuManager
{
    private const string VerbName = "ManagedDriveImportArchive";

    private static readonly string[] SupportedExtensions = [".zip", ".7z", ".rar", ".tar"];

    /// <summary>
    /// Gets a value indicating whether the context menu entry is currently registered for the
    /// current executable's location. A stale entry pointing at a different (e.g. relocated)
    /// installation is treated as not registered.
    /// </summary>
    public static bool IsRegistered
    {
        get
        {
            var exeDir = GetExeDirectory();
            if (exeDir == null)
            {
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(CommandKeyPath(SupportedExtensions[0]), writable: false);
            if (key?.GetValue(null) is not string command)
            {
                return false;
            }

            return command.Contains(exeDir, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Registers or unregisters the Explorer context menu entry for every supported archive
    /// extension.
    /// </summary>
    /// <param name="enable"><c>true</c> to add the entries; <c>false</c> to remove them.</param>
    public static void SetEnabled(bool enable)
    {
        if (!enable)
        {
            foreach (var extension in SupportedExtensions)
            {
                Registry.CurrentUser.DeleteSubKeyTree(ShellKeyPath(extension), throwOnMissingSubKey: false);
            }

            return;
        }

        var exeDir = GetExeDirectory();
        if (exeDir == null)
        {
            return;
        }

        var managedDriveExePath = Path.Combine(exeDir, "ManagedDrive.exe");
        var verbText = Loc.Get("ShellMenu.ImportArchive");

        foreach (var extension in SupportedExtensions)
        {
            using var shellKey = Registry.CurrentUser.CreateSubKey(ShellKeyPath(extension));
            shellKey.SetValue("MUIVerb", verbText);
            shellKey.SetValue("Icon", $"\"{managedDriveExePath}\",0");

            using var commandKey = shellKey.CreateSubKey("command");
            commandKey.SetValue(null, $"\"{managedDriveExePath}\" mount-archive \"%1\"");
        }
    }

    private static string? GetExeDirectory()
    {
        var exePath = Environment.ProcessPath;
        return string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(exePath);
    }

    private static string ShellKeyPath(string extension) =>
        $@"Software\Classes\SystemFileAssociations\{extension}\shell\{VerbName}";

    private static string CommandKeyPath(string extension) =>
        $@"{ShellKeyPath(extension)}\command";
}
