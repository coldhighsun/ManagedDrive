using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ManagedDrive.WingetExtension;

/// <summary>
/// Detects whether a path lives on a WinFsp volume. Resolves the path to its NT form via
/// <c>GetFinalPathNameByHandle</c>, which follows drive letters and directory mount points
/// alike: WinFsp volumes resolve to <c>\Device\Volume{GUID}\...</c>, ordinary partitions to
/// <c>\Device\HarddiskVolumeN\...</c>.
/// </summary>
internal static partial class WinFspVolumeDetector
{
    /// <summary>
    /// <c>CreateFile</c> flag required to open a directory handle.
    /// </summary>
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>
    /// <c>CreateFile</c> disposition that only opens an existing file or directory.
    /// </summary>
    private const uint OpenExisting = 3;

    /// <summary>
    /// <c>FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE</c>, so the probe never blocks
    /// anyone else using the directory.
    /// </summary>
    private const uint FileShareAll = 0x7;

    /// <summary>
    /// <c>GetFinalPathNameByHandle</c> flag asking for the NT device path
    /// (<c>VOLUME_NAME_NT</c>) instead of a DOS path.
    /// </summary>
    private const uint VolumeNameNt = 0x2;

    /// <summary>
    /// Matches the NT path of anything on a WinFsp volume.
    /// </summary>
    [GeneratedRegex(@"^\\Device\\Volume\{[0-9A-Fa-f-]+\}(\\|$)")]
    private static partial Regex WinFspDevicePathPattern();

    /// <summary>
    /// Returns whether the current process's TEMP directory is on a WinFsp volume, whether it is
    /// reached through a drive letter or a directory mount point.
    /// </summary>
    /// <returns><c>true</c> if TEMP is on a WinFsp volume.</returns>
    public static bool IsCurrentTempOnWinFspVolume() => IsOnWinFspVolume(Path.GetTempPath());

    /// <summary>
    /// Returns whether <paramref name="path"/> is on a WinFsp volume. A path that doesn't exist
    /// yet is judged by its nearest existing ancestor.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>
    /// <c>true</c> if the path is on a WinFsp volume; <c>false</c> otherwise, including when it
    /// is malformed or none of it can be opened.
    /// </returns>
    public static bool IsOnWinFspVolume(string path) =>
        TryGetNtPath(path, out var ntPath) && IsWinFspDevicePath(ntPath);

    /// <summary>
    /// Returns whether an NT path (as returned for <c>VOLUME_NAME_NT</c>) is on a WinFsp volume.
    /// </summary>
    /// <param name="ntPath">The NT path, e.g. <c>\Device\Volume{GUID}\Temp</c>.</param>
    /// <returns><c>true</c> if <paramref name="ntPath"/> is on a WinFsp volume.</returns>
    internal static bool IsWinFspDevicePath(string ntPath) => WinFspDevicePathPattern().IsMatch(ntPath);

    /// <summary>
    /// Resolves the nearest existing ancestor of <paramref name="path"/> (or the path itself) to
    /// its NT device path.
    /// </summary>
    /// <param name="path">The path to resolve.</param>
    /// <param name="ntPath">The resolved NT path, when this returns <c>true</c>.</param>
    /// <returns><c>false</c> if the path is malformed or can't be opened.</returns>
    private static bool TryGetNtPath(string path, out string ntPath)
    {
        ntPath = string.Empty;

        string? current;
        try
        {
            current = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        while (current is not null && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        if (current is null)
        {
            return false;
        }

        using var handle = CreateFileW(
            current, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return false;
        }

        var buffer = new char[512];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, VolumeNameNt);
            if (length == 0)
            {
                return false;
            }

            // A result that doesn't fit is the required size, including the terminating null.
            if (length < buffer.Length)
            {
                ntPath = new string(buffer, 0, (int)length);
                return true;
            }

            buffer = new char[length];
        }
    }

    /// <summary>
    /// Opens a file or directory handle.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    /// <summary>
    /// Retrieves the final path of an open file or directory.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, char[] filePath, uint filePathLength, uint flags);
}
