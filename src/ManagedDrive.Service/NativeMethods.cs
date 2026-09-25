using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ManagedDrive.Service;

/// <summary>
/// The user on the other end of a pipe connection.
/// </summary>
/// <param name="UserSid">SID of the user.</param>
/// <param name="GroupSids">SIDs of the user's groups, both enabled and deny-only.</param>
internal sealed record PipeClientIdentity(string UserSid, IReadOnlyList<string> GroupSids);

/// <summary>
/// P/Invoke surface for the privileged DOS-device operations. Because this process runs as
/// LocalSystem, <see cref="DefineDosDevice"/> targets the global <c>\GLOBAL??\</c> object
/// namespace, making the symlink visible to every session — which is the whole point of the
/// helper service.
/// </summary>
internal static class NativeMethods
{
    public const uint DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;
    public const uint DDD_RAW_TARGET_PATH = 0x00000001;
    public const uint DDD_REMOVE_DEFINITION = 0x00000002;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint GENERIC_NONE = 0;
    private const uint OPEN_EXISTING = 3;

    /// <summary>
    /// Win32 error returned by <see cref="QueryDosDevice"/> when the name has no definition.
    /// </summary>
    private const int ERROR_FILE_NOT_FOUND = 2;

    /// <summary>
    /// Win32 error returned when part of a path (here: the device) doesn't exist.
    /// </summary>
    private const int ERROR_PATH_NOT_FOUND = 3;

    /// <summary>
    /// Win32 error returned by <see cref="QueryDosDevice"/> when the target buffer is too small.
    /// </summary>
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>
    /// Initial <see cref="QueryDosDevice"/> buffer size, in characters.
    /// </summary>
    private const int QueryDosDeviceInitialChars = 1024;

    /// <summary>
    /// Largest <see cref="QueryDosDevice"/> buffer tried, in characters, before giving up.
    /// </summary>
    private const int QueryDosDeviceMaxChars = 64 * 1024;

    /// <summary>
    /// Access right needed to query a token's user SID.
    /// </summary>
    private const uint TOKEN_QUERY = 0x0008;

    /// <summary>
    /// Creates a global DOS-device symlink <paramref name="letter"/> → <paramref name="devicePath"/>.
    /// </summary>
    public static bool CreateGlobalSymlink(string letter, string devicePath) =>
        DefineDosDevice(DDD_RAW_TARGET_PATH, letter, devicePath);

    /// <summary>
    /// Probes whether the underlying NT volume device is still present, independent of any
    /// drive-letter symlink, by opening it through the <c>GLOBALROOT</c> device namespace. Only a
    /// "not found" failure counts as absent: a live volume whose open fails for another reason
    /// (access denied, a busy or stalled file system) must not be treated as gone, or its letter
    /// would be purged and handed to someone else.
    /// </summary>
    public static bool DeviceExists(string devicePath)
    {
        using var handle = CreateFile(
            @"\\?\GLOBALROOT" + devicePath,
            GENERIC_NONE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        return !handle.IsInvalid || !IsDeviceNotFoundError(Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Whether a failed open of a device path means the device doesn't exist.
    /// </summary>
    /// <param name="error">The Win32 error of the failed open.</param>
    /// <returns><c>true</c> for "file not found" and "path not found".</returns>
    internal static bool IsDeviceNotFoundError(int error) =>
        error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND;

    /// <summary>
    /// Returns the PID of the process connected to the server end of <paramref name="pipeHandle"/>,
    /// or -1 if it cannot be determined.
    /// </summary>
    public static int GetClientProcessId(SafeHandle pipeHandle) =>
        GetNamedPipeClientProcessId(pipeHandle.DangerousGetHandle(), out var pid) ? (int)pid : -1;

    /// <summary>
    /// Returns the identity of the user connected to <paramref name="pipe"/>, or <c>null</c> if
    /// it cannot be determined. Must be called after data has been read from the pipe (a
    /// requirement of named-pipe impersonation). Only the impersonation token is opened while
    /// impersonating — the client connects at identification level, under which the thread can't
    /// load assemblies or open files, so the SIDs are read after reverting.
    /// </summary>
    public static PipeClientIdentity? GetClientIdentity(NamedPipeServerStream pipe)
    {
        SafeAccessTokenHandle? token = null;
        pipe.RunAsClient(() =>
        {
            // openAsSelf: check access against the service's own token, not the client's.
            if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, openAsSelf: true, out token))
            {
                token = null;
            }
        });

        if (token == null)
        {
            return null;
        }

        using (token)
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            return identity.User is { } user ? new(user.Value, GetGroupSids(identity)) : null;
        }
    }

    /// <summary>
    /// Returns the SIDs of <paramref name="identity"/>'s groups, including deny-only ones (such
    /// as <c>BUILTIN\Administrators</c> in a UAC-filtered token), which
    /// <see cref="WindowsIdentity.Groups"/> leaves out.
    /// </summary>
    /// <param name="identity">The identity to read.</param>
    /// <returns>The group SIDs.</returns>
    internal static IReadOnlyList<string> GetGroupSids(WindowsIdentity identity) =>
        [.. identity.Claims
            .Where(c => c.Type is ClaimTypes.GroupSid or ClaimTypes.DenyOnlySid)
            .Select(c => c.Value)];

    /// <summary>
    /// Returns every definition of <paramref name="letter"/>, current one first, followed by the
    /// earlier definitions it is stacked on.
    /// </summary>
    /// <param name="letter">The drive letter, in <c>"X:"</c> form.</param>
    /// <returns>
    /// The targets (empty if the letter is undefined), or <c>null</c> if they could not be queried.
    /// </returns>
    public static IReadOnlyList<string>? QueryDosDeviceTargets(string letter)
    {
        for (var size = QueryDosDeviceInitialChars; size <= QueryDosDeviceMaxChars; size *= 2)
        {
            var buffer = new char[size];
            var chars = QueryDosDevice(letter, buffer, (uint)buffer.Length);
            var error = chars == 0 ? Marshal.GetLastWin32Error() : 0;
            if (error != ERROR_INSUFFICIENT_BUFFER)
            {
                return ParseQueryDosDeviceResult(buffer, chars, error);
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes the result of a <see cref="QueryDosDevice"/> call.
    /// </summary>
    /// <param name="buffer">The buffer passed to the call.</param>
    /// <param name="charsReturned">The call's return value.</param>
    /// <param name="lastError">The Win32 error after the call, when it returned 0.</param>
    /// <returns>
    /// The null-separated targets in <paramref name="buffer"/>; an empty list if the name is not
    /// defined; or <c>null</c> for any other failure, whose meaning is unknown.
    /// </returns>
    internal static IReadOnlyList<string>? ParseQueryDosDeviceResult(char[] buffer, uint charsReturned, int lastError)
    {
        if (charsReturned == 0)
        {
            return lastError == ERROR_FILE_NOT_FOUND ? [] : null;
        }

        return new string(buffer, 0, (int)charsReturned).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool DefineDosDevice(uint flags, string deviceName, string? targetPath);

    /// <summary>
    /// Returns a pseudo-handle for the calling thread; it needs no closing.
    /// </summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    /// <summary>
    /// Opens the access token of a thread — while impersonating, the impersonated client's token.
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenThreadToken(
        IntPtr threadHandle, uint desiredAccess, bool openAsSelf, out SafeAccessTokenHandle tokenHandle);

    /// <summary>
    /// Retrieves the null-separated targets of a DOS-device name into <paramref name="targetPath"/>,
    /// returning the number of characters written, or 0 on failure.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string deviceName, char[] targetPath, uint max);
}
