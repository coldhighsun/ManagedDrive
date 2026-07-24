using BenchmarkDotNet.Attributes;

namespace ManagedDrive.Benchmarks;

/// <summary>
/// Measures contention on <see cref="FileNodeMap"/>'s single global lock under concurrent access
/// from multiple threads to disjoint files/directories on the same RAM disk. Data-gathering step
/// for evaluating whether swapping to a <see cref="System.Threading.ReaderWriterLockSlim"/> is
/// worth the added complexity — see the performance optimization plan.
/// </summary>
[SimpleJob(warmupCount: 2, iterationCount: 3)]
[MemoryDiagnoser]
[MinColumn, MaxColumn]
public class ConcurrentAccessBenchmarks
{
    private const ulong CapacityBytes = 256 * 1024 * 1024;
    private const int FileBytes = 4 * 1024;
    private const int FilesPerThread = 50;
    private static readonly int ThreadCount = Environment.ProcessorCount;
    private string _mountPoint = null!;
    private RamDisk _ramDisk = null!;
    private byte[] _writeBuffer = null!;

    [GlobalCleanup]
    public void Cleanup() => _ramDisk.Dispose();

    [IterationSetup(Targets = [nameof(ConcurrentReads_DisjointDirectories)])]
    public void IterationSetup_ConcurrentReads()
    {
        for (var t = 0; t < ThreadCount; t++)
        {
            var dir = Path.Combine(_mountPoint + @"\", $"read-dir-{t}");
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            for (var i = 0; i < FilesPerThread; i++)
                File.WriteAllBytes(Path.Combine(dir, $"file-{i}.dat"), _writeBuffer);
        }
    }

    [IterationSetup(Targets = [nameof(ConcurrentWrites_DisjointDirectories)])]
    public void IterationSetup_ConcurrentWrites()
    {
        for (var t = 0; t < ThreadCount; t++)
        {
            var dir = Path.Combine(_mountPoint + @"\", $"write-dir-{t}");
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }
    }

    [IterationSetup(Targets = [nameof(SingleThreaded_Read)])]
    public void IterationSetup_SingleThreadedRead()
    {
        var dir = Path.Combine(_mountPoint + @"\", "baseline-read-dir");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        for (var i = 0; i < ThreadCount * FilesPerThread; i++)
            File.WriteAllBytes(Path.Combine(dir, $"file-{i}.dat"), _writeBuffer);
    }

    [IterationSetup(Targets = [nameof(SingleThreaded_Write)])]
    public void IterationSetup_SingleThreadedWrite()
    {
        var dir = Path.Combine(_mountPoint + @"\", "baseline-write-dir");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Each thread reads its own directory's files — no data dependency across threads, so any
    /// slowdown relative to single-threaded throughput reflects lock contention in <see cref="FileNodeMap"/>,
    /// not genuine data sharing.
    /// </summary>
    [Benchmark(Description = "ConcurrentReads DisjointDirectories", Baseline = true)]
    public void ConcurrentReads_DisjointDirectories()
    {
        Parallel.For(0, ThreadCount, t =>
        {
            var dir = Path.Combine(_mountPoint + @"\", $"read-dir-{t}");
            var buffer = new byte[FileBytes];
            for (var i = 0; i < FilesPerThread; i++)
            {
                using var fs = new FileStream(Path.Combine(dir, $"file-{i}.dat"), FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.ReadExactly(buffer, 0, buffer.Length);
            }
        });
    }

    /// <summary>
    /// Each thread creates and writes files in its own directory — exercises <c>Add</c>/allocation
    /// tracking under concurrent access to disjoint paths.
    /// </summary>
    [Benchmark(Description = "ConcurrentWrites DisjointDirectories")]
    public void ConcurrentWrites_DisjointDirectories()
    {
        Parallel.For(0, ThreadCount, t =>
        {
            var dir = Path.Combine(_mountPoint + @"\", $"write-dir-{t}");
            for (var i = 0; i < FilesPerThread; i++)
            {
                using var fs = new FileStream(Path.Combine(dir, $"file-{i}.dat"), FileMode.Create, FileAccess.Write, FileShare.None);
                fs.Write(_writeBuffer, 0, _writeBuffer.Length);
            }
        });
    }

    /// <summary>
    /// Sequential read baseline: reads the same total file count as <see cref="ConcurrentReads_DisjointDirectories"/>
    /// (<c>ThreadCount * FilesPerThread</c>) on a single thread, so the parallel version's speedup can be
    /// computed directly instead of eyeballed against an unrelated workload size.
    /// </summary>
    [Benchmark(Description = "SingleThreaded Read")]
    public void SingleThreaded_Read()
    {
        var dir = Path.Combine(_mountPoint + @"\", "baseline-read-dir");
        var buffer = new byte[FileBytes];
        for (var i = 0; i < ThreadCount * FilesPerThread; i++)
        {
            using var fs = new FileStream(Path.Combine(dir, $"file-{i}.dat"), FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.ReadExactly(buffer, 0, buffer.Length);
        }
    }

    /// <summary>
    /// Sequential write baseline: creates and writes the same total file count as
    /// <see cref="ConcurrentWrites_DisjointDirectories"/> on a single thread. Comparing this against the
    /// parallel version isolates whether the parallel slowdown comes from <see cref="FileNodeMap"/> lock
    /// contention or is simply the inherent per-file <c>Create</c> cost multiplied by the file count.
    /// </summary>
    [Benchmark(Description = "SingleThreaded Write")]
    public void SingleThreaded_Write()
    {
        var dir = Path.Combine(_mountPoint + @"\", "baseline-write-dir");
        for (var i = 0; i < ThreadCount * FilesPerThread; i++)
        {
            using var fs = new FileStream(Path.Combine(dir, $"file-{i}.dat"), FileMode.Create, FileAccess.Write, FileShare.None);
            fs.Write(_writeBuffer, 0, _writeBuffer.Length);
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        _mountPoint = DriveLetterHelper.FindFreeMountPoint();
        _ramDisk = RamDisk.Create(new()
        {
            CapacityBytes = CapacityBytes,
            MountPoint = _mountPoint,
            VolumeLabel = "ConcurrentBenchDisk",
        });

        _writeBuffer = new byte[FileBytes];
        new Random(7).NextBytes(_writeBuffer);
    }
}
