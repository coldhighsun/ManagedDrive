using System.Runtime.InteropServices;

namespace ManagedDrive.App.Services;

/// <summary>
/// Resets the current user's TEMP and TMP environment variables to their Windows defaults
/// and broadcasts the change to all running processes via WM_SETTINGCHANGE.
/// </summary>
public static class TempDirResetService
{
    // Stored unexpanded so the registry value remains portable across user profiles.
    private const string DefaultUserTemp = @"%USERPROFILE%\AppData\Local\Temp";

    private const uint SendMessageTimeoutAbortIfHung = 0x0002;
    private const string UserEnvKeyPath = "Environment";
    private const uint WmSettingChange = 0x001A;
    private static readonly IntPtr HwndBroadcast = new(-1);

    /// <summary>
    /// Writes the default values to <c>HKCU\Environment</c> and broadcasts
    /// <c>WM_SETTINGCHANGE</c> so running processes pick up the change.
    /// </summary>
    /// <returns><c>true</c> on success; <c>false</c> if the registry write failed.</returns>
    public static bool Reset()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserEnvKeyPath, writable: true);
            if (key == null)
            {
                return false;
            }

            key.SetValue("TEMP", DefaultUserTemp, RegistryValueKind.ExpandString);
            key.SetValue("TMP", DefaultUserTemp, RegistryValueKind.ExpandString);

            SendMessageTimeout(HwndBroadcast, WmSettingChange, UIntPtr.Zero, "Environment",
                SendMessageTimeoutAbortIfHung, 5000, out _);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the current user's TEMP and TMP environment variables to <paramref name="tempPath"/>,
    /// creating the directory first if it does not already exist.
    /// </summary>
    /// <param name="tempPath">The absolute path to use as the temp directory.</param>
    /// <returns><c>true</c> on success; <c>false</c> if the directory or registry write failed.</returns>
    public static bool Set(string tempPath)
    {
        try
        {
            Directory.CreateDirectory(tempPath);

            using var key = Registry.CurrentUser.OpenSubKey(UserEnvKeyPath, writable: true);
            if (key == null)
            {
                return false;
            }

            key.SetValue("TEMP", tempPath, RegistryValueKind.String);
            key.SetValue("TMP", tempPath, RegistryValueKind.String);

            SendMessageTimeout(HwndBroadcast, WmSettingChange, UIntPtr.Zero, "Environment",
                SendMessageTimeoutAbortIfHung, 5000, out _);

            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);
}