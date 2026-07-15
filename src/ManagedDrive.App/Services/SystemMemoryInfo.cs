using System.Runtime.InteropServices;

namespace ManagedDrive.App.Services;

/// <summary>
/// Reads system-wide physical memory availability via the Win32 API, mirroring what
/// Task Manager reports (unlike <see cref="GC.GetGCMemoryInfo()"/>, which reflects the
/// .NET GC's own memory limit rather than true system-wide available RAM).
/// </summary>
public static class SystemMemoryInfo
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}