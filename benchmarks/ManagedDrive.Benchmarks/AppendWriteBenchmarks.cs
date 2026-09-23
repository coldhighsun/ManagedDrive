using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Fsp;
using ManagedDrive.Core.Diagnostics;
using ManagedDrive.Core.FileSystem;

namespace ManagedDrive.Benchmarks;

/// <summary>
/// Drives <see cref="MemoryFileSystem.Write"/> in-process (no WinFsp mount) with a streaming
/// append pattern, where nearly every write extends the file's allocation and therefore runs the
/// low-memory guard. Comparing <see cref="MemoryCheck.Syscall"/> (the production
/// <see cref="SystemMemoryInfo.GetAvailablePhysicalBytes"/>) against
/// <see cref="MemoryCheck.Constant"/> isolates what that per-extension syscall costs.
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
[MinColumn, MaxColumn]
public class AppendWriteBenchmarks
{
    public enum MemoryCheck
    {
        Syscall,
        Constant,
    }

    private const int TotalBytes = 4 * 1024 * 1024;
    private const string FileName = "\\append.bin";

    private MemoryFileSystem _fs = null!;
    private IntPtr _buffer;

    [Params(512, 4 * 1024, 64 * 1024)]
    public int ChunkBytes { get; set; }

    [Params(MemoryCheck.Syscall, MemoryCheck.Constant)]
    public MemoryCheck Check { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Func<ulong>? provider = Check == MemoryCheck.Constant ? () => ulong.MaxValue / 2 : null;
        _fs = new(maxCapacity: 1UL << 30, "Bench", availableMemoryProvider: provider);
        _fs.Init(null!);

        var pattern = new byte[ChunkBytes];
        Array.Fill(pattern, (byte)0xAB);
        _buffer = Marshal.AllocHGlobal(ChunkBytes);
        Marshal.Copy(pattern, 0, _buffer, ChunkBytes);
    }

    [GlobalCleanup]
    public void Cleanup() => Marshal.FreeHGlobal(_buffer);

    /// <summary>
    /// Creates an empty file, appends <see cref="TotalBytes"/> in <see cref="ChunkBytes"/>-sized
    /// writes, then deletes it so every invocation starts from the same empty state.
    /// </summary>
    [Benchmark]
    public void AppendThenDelete()
    {
        _fs.Create(FileName, 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var node, out _, out _, out _);

        for (var written = 0; written < TotalBytes; written += ChunkBytes)
        {
            _fs.Write(node!, null!, _buffer, 0, (uint)ChunkBytes,
                writeToEndOfFile: true, constrainedIo: false, out _, out _);
        }

        _fs.Cleanup(node!, null!, FileName, FileSystemBase.CleanupDelete);
    }

    /// <summary>
    /// Raw cost of one <c>GlobalMemoryStatusEx</c> call, for reference against the per-write
    /// numbers above (only meaningful in the <see cref="MemoryCheck.Syscall"/> rows).
    /// </summary>
    [Benchmark]
    public ulong QueryAvailableMemory() => SystemMemoryInfo.GetAvailablePhysicalBytes();
}
