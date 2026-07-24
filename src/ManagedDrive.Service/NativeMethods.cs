using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ManagedDrive.Service;

/// <summary>
/// P/Invoke surface for the privileged DOS-device operations. Because this process runs as
/// LocalSystem, <see cref="DefineDosDevice"/> targets the global <c>\GLOBAL??\</c> object
/// namespace, making the symlink visible to every session — which is the whole point of the
/// helper service.
/// </summary>
internal static class NativeMethods
{
    public const uint DDD_RAW_TARGET_PATH = 0x00000001;
    public const uint DDD_REMOVE_DEFINITION = 0x00000002;
    public const uint DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;

    private const uint GENERIC_NONE = 0;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    /// <summary>
    /// Creates a global DOS-device symlink <paramref name="letter"/> → <paramref name="devicePath"/>.
    /// </summary>
    public static bool CreateGlobalSymlink(string letter, string devicePath) =>
        DefineDosDevice(DDD_RAW_TARGET_PATH, letter, devicePath);

    /// <summary>
    /// Removes the global DOS-device symlink for <paramref name="letter"/>, matching exactly
    /// <paramref name="devicePath"/> so an unrelated definition on the same letter is never
    /// clobbered.
    /// </summary>
    public static bool RemoveGlobalSymlink(string letter, string devicePath) =>
        DefineDosDevice(
            DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION | DDD_EXACT_MATCH_ON_REMOVE,
            letter,
            devicePath);

    /// <summary>
    /// Probes whether the underlying NT volume device is still present, independent of any
    /// drive-letter symlink, by opening it through the <c>GLOBALROOT</c> device namespace.
    /// </summary>
    public static bool DeviceExists(string devicePath)
    {
        using SafeFileHandle handle = CreateFile(
            @"\\?\GLOBALROOT" + devicePath,
            GENERIC_NONE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        return !handle.IsInvalid;
    }

    /// <summary>
    /// Returns the PID of the process connected to the server end of <paramref name="pipeHandle"/>,
    /// or -1 if it cannot be determined.
    /// </summary>
    public static int GetClientProcessId(SafeHandle pipeHandle) =>
        GetNamedPipeClientProcessId(pipeHandle.DangerousGetHandle(), out var pid) ? (int)pid : -1;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool DefineDosDevice(uint flags, string deviceName, string? targetPath);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
