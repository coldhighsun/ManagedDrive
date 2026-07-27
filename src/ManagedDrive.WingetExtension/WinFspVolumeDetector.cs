using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ManagedDrive.WingetExtension;

// WinFsp volumes resolve via QueryDosDevice to \Device\Volume{GUID};
// ordinary partitions resolve to \Device\HarddiskVolumeN.
internal static partial class WinFspVolumeDetector
{
    [GeneratedRegex(@"^\\Device\\Volume\{[0-9A-Fa-f-]+\}$")]
    private static partial Regex WinFspDevicePattern();

    public static bool IsWinFspVolume(string driveLetter)
    {
        var buffer = new char[1024];
        var length = QueryDosDevice(driveLetter, buffer, (uint)buffer.Length);
        if (length == 0)
        {
            return false;
        }

        var devicePath = new string(buffer, 0, (int)length).Split('\0', 2)[0];
        return WinFspDevicePattern().IsMatch(devicePath);
    }

    // Returns false (not a WinFsp volume) if TEMP isn't set or doesn't start with a drive letter,
    // since there's nothing to route around in that case.
    public static bool IsCurrentTempOnWinFspVolume()
    {
        var tempPath = Path.GetTempPath();
        if (tempPath.Length < 2 || tempPath[1] != ':')
        {
            return false;
        }

        var driveLetter = $"{tempPath[0]}:";
        return IsWinFspVolume(driveLetter);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint QueryDosDevice(string deviceName, char[] targetPath, uint max);
}
