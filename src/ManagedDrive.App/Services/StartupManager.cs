namespace ManagedDrive.App.Services;

/// <summary>
/// Manages the Windows startup registry entry for ManagedDrive.
/// Reads and writes <c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run</c>
/// without requiring elevated privileges.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ManagedDrive";

    /// <summary>
    /// Gets a value indicating whether ManagedDrive is registered to start with Windows.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>
    /// Registers or unregisters ManagedDrive in the Windows startup run key.
    /// </summary>
    /// <param name="enable">
    /// <c>true</c> to add the startup entry; <c>false</c> to remove it.
    /// </param>
    public static void SetEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key == null)
        {
            return;
        }

        if (enable)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}