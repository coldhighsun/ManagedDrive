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
    /// Sends the environment-change broadcast off the caller's thread: it waits on every top-level
    /// window, so one hung window would otherwise freeze the caller for seconds.
    /// </summary>
    private static readonly CoalescingBroadcaster Broadcaster = new(() =>
        SendMessageTimeout(HwndBroadcast, WmSettingChange, UIntPtr.Zero, "Environment",
            SendMessageTimeoutAbortIfHung, 5000, out _));

    /// <summary>
    /// Captures the current user's TEMP and TMP registry values exactly as stored (unexpanded, with their
    /// value kinds) so <see cref="Restore"/> can put them back byte for byte.
    /// </summary>
    /// <returns>The snapshot, or <see langword="null"/> when the registry could not be read.</returns>
    public static UserTempSnapshot? Capture()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserEnvKeyPath);
            return key is null ? null : new UserTempSnapshot(ReadValue(key, "TEMP"), ReadValue(key, "TMP"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="snapshot"/> back to <c>HKCU\Environment</c>, deleting a value that was not set
    /// when it was captured, and broadcasts <c>WM_SETTINGCHANGE</c>. Unlike <see cref="Set"/>, it creates no
    /// directory and keeps each value's original kind, so an unexpanded <c>%VAR%</c> path stays unexpanded.
    /// </summary>
    /// <param name="snapshot">The values to restore.</param>
    /// <returns><c>true</c> on success; <c>false</c> if the registry write failed.</returns>
    public static bool Restore(UserTempSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserEnvKeyPath, writable: true);
            if (key == null)
            {
                return false;
            }

            WriteValue(key, "TEMP", snapshot.Temp);
            WriteValue(key, "TMP", snapshot.Tmp);
            UserTempCache.Shared.Invalidate();

            BroadcastEnvironmentChange();

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads one value unexpanded together with its kind, or <see langword="null"/> when it is not set.
    /// </summary>
    private static UserTempValue? ReadValue(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string text
            ? new UserTempValue(text, key.GetValueKind(name))
            : null;

    /// <summary>
    /// Writes <paramref name="value"/> back under <paramref name="name"/>, or deletes the value when it was not set.
    /// </summary>
    private static void WriteValue(RegistryKey key, string name, UserTempValue? value)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value.Text, value.Kind);
        }
    }

    /// <summary>
    /// Writes the default values to <c>HKCU\Environment</c> and broadcasts
    /// <c>WM_SETTINGCHANGE</c> so running processes pick up the change.
    /// </summary>
    /// <param name="broadcast">
    /// Whether to announce the change to running programs. <c>false</c> when the session is ending
    /// and nothing is left to notify; only the registry write matters then.
    /// </param>
    /// <returns><c>true</c> on success; <c>false</c> if the registry write failed.</returns>
    public static bool Reset(bool broadcast = true)
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
            UserTempCache.Shared.Invalidate();

            if (broadcast)
            {
                BroadcastEnvironmentChange();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tells running programs that the user's environment variables changed.
    /// </summary>
    internal static void BroadcastEnvironmentChange() => Broadcaster.Request();

    /// <summary>
    /// Waits for broadcasts requested so far to be delivered, so the last change is announced before
    /// the process ends.
    /// </summary>
    /// <param name="timeout">The longest to wait.</param>
    /// <returns><c>true</c> if all of them were sent.</returns>
    public static bool WaitForPendingBroadcasts(TimeSpan timeout) => Broadcaster.Wait(timeout);

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

/// <summary>
/// What a "restore TEMP" request did.
/// </summary>
public enum TempRestoreResult
{
    /// <summary>TEMP and TMP do not point into a RAM disk and nothing was recorded, so nothing was changed.</summary>
    NothingToRestore,

    /// <summary>TEMP and TMP were put back.</summary>
    Restored,

    /// <summary>TEMP and TMP could not be written.</summary>
    Failed,
}

/// <summary>
/// One stored environment value: its text exactly as in the registry and its value kind.
/// </summary>
/// <param name="Text">The stored text, unexpanded.</param>
/// <param name="Kind">The registry value kind (<c>String</c> or <c>ExpandString</c>).</param>
public sealed record UserTempValue(string Text, RegistryValueKind Kind);

/// <summary>
/// The user's TEMP and TMP registry values as captured by <see cref="TempDirResetService.Capture"/>.
/// </summary>
/// <param name="Temp">The TEMP value, or <see langword="null"/> when it was not set.</param>
/// <param name="Tmp">The TMP value, or <see langword="null"/> when it was not set.</param>
public sealed record UserTempSnapshot(UserTempValue? Temp, UserTempValue? Tmp);
