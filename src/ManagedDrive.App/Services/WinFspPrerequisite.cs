namespace ManagedDrive.App.Services;

/// <summary>
/// Checks whether a compatible WinFsp 2.2.x runtime is installed. Extracted from
/// <see cref="App.CheckWinFspPrerequisite"/> as a pure, static check; the caller decides what to
/// do (show a dialog, offer the download page, shut down) when it reports missing.
/// </summary>
public static class WinFspPrerequisite
{
    /// <summary>
    /// Returns <c>true</c> when the system-installed <c>winfsp-msil.dll</c> at
    /// <c>C:\Program Files (x86)\WinFsp\bin\</c> is present and reports a 2.2.x file version.
    /// </summary>
    public static bool IsInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp")
                        ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");

        var dllPath = key?.GetValue("InstallDir") is string installDir
            ? Path.Combine(installDir, "bin", "winfsp-msil.dll")
            : null;

        return dllPath is not null &&
            File.Exists(dllPath) &&
            FileVersionInfo.GetVersionInfo(dllPath).FileVersion?.StartsWith("2.2.") == true;
    }
}