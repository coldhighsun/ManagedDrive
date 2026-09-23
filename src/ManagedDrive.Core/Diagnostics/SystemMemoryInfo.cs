using System.Runtime.InteropServices;

namespace ManagedDrive.Core.Diagnostics;

/// <summary>
/// Reads system-wide physical memory availability via the Win32 API, mirroring what
/// Task Manager reports (unlike <see cref="GC.GetGCMemoryInfo()"/>, which reflects the
/// .NET GC's own memory limit rather than true system-wide available RAM).
/// </summary>
public static partial class SystemMemoryInfo
{
    /// <summary>
    /// Gets the amount of physical memory currently available, in bytes.
    /// </summary>
    /// <returns>The available physical memory in bytes, or <c>0</c> if the query failed.</returns>
    public static ulong GetAvailablePhysicalBytes()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullAvailPhys : 0;
    }

    // Source-generated marshaling (LibraryImport) instead of DllImport: no reflection-based stub
    // built at first call, and the marshaling code is checked at compile time.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// Native <c>MEMORYSTATUSEX</c> structure passed to <see cref="GlobalMemoryStatusEx"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        /// <summary>
        /// Size of this structure, in bytes, as required by <see cref="GlobalMemoryStatusEx"/>.
        /// </summary>
        public uint dwLength;

        /// <summary>
        /// Approximate percentage of physical memory currently in use.
        /// </summary>
        public uint dwMemoryLoad;

        /// <summary>
        /// Total physical memory, in bytes.
        /// </summary>
        public ulong ullTotalPhys;

        /// <summary>
        /// Available physical memory, in bytes.
        /// </summary>
        public ulong ullAvailPhys;

        /// <summary>
        /// Total size of the page file, in bytes.
        /// </summary>
        public ulong ullTotalPageFile;

        /// <summary>
        /// Available size of the page file, in bytes.
        /// </summary>
        public ulong ullAvailPageFile;

        /// <summary>
        /// Total size of the user-mode portion of the virtual address space, in bytes.
        /// </summary>
        public ulong ullTotalVirtual;

        /// <summary>
        /// Available size of the user-mode portion of the virtual address space, in bytes.
        /// </summary>
        public ulong ullAvailVirtual;

        /// <summary>
        /// Reserved; always zero.
        /// </summary>
        public ulong ullAvailExtendedVirtual;
    }
}
