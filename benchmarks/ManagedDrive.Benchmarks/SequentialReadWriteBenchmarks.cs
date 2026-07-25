using BenchmarkDotNet.Attributes;

namespace ManagedDrive.Benchmarks;

[SimpleJob(warmupCount: 2, iterationCount: 3)]
[MemoryDiagnoser]
[MinColumn, MaxColumn]
public class SequentialReadWriteBenchmarks
{
    private const ulong CapacityBytes = 128 * 1024 * 1024;
    private RamDisk _ramDisk = null!;

    private string _ramFile = null!;

    private byte[] _readBuffer = null!;

    private string _tempDir = null!;

    private string _tempFile = null!;

    private byte[] _writeBuffer = null!;

    [Params(4 * 1024, 1 * 1024 * 1024)]
    public int FileSizeBytes
    {
        get; set;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (File.Exists(_ramFile))
            File.Delete(_ramFile);
        _ramDisk.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // NOTE: This reads a file that was just written in Setup(), so the data is still resident in
    // the Windows unified page cache (kernel-side RAM). It therefore measures an OS-cache hit, not
    // a real SSD read — no I/O reaches the disk. Kept as a baseline; compare against the "(uncached)"
    // variant below for actual medium throughput.
    [Benchmark(Description = "PhysicalDisk Read (OS cache)")]
    public void PhysicalDisk_SequentialRead()
    {
        using var fs = new FileStream(_tempFile, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan);
        fs.ReadExactly(_readBuffer, 0, _readBuffer.Length);
    }

    // Bypasses the OS page cache (FILE_FLAG_NO_BUFFERING) so the read hits the physical SSD. This is
    // the honest comparison point against the RAM disk: the RAM disk goes through user-mode WinFsp on
    // every read, so it cannot beat an OS-cache hit, but it should beat a genuine uncached SSD read.
    [Benchmark(Description = "PhysicalDisk Read (uncached)")]
    public void PhysicalDisk_SequentialReadUncached() =>
        UnbufferedIo.ReadFull(_tempFile, FileSizeBytes);

    [Benchmark(Description = "PhysicalDisk Write", Baseline = true)]
    public void PhysicalDisk_SequentialWrite()
    {
        using var fs = new FileStream(_tempFile, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 4096, FileOptions.WriteThrough);
        fs.Write(_writeBuffer, 0, _writeBuffer.Length);
    }

    [Benchmark(Description = "RamDisk Read (OS cache)")]
    public void RamDisk_SequentialRead()
    {
        using var fs = new FileStream(_ramFile, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan);
        fs.ReadExactly(_readBuffer, 0, _readBuffer.Length);
    }

    // Forces every read through the WinFsp user-mode round-trip (no OS cache to absorb it), isolating
    // the RAM disk's raw read path for an apples-to-apples comparison with "PhysicalDisk Read (uncached)".
    [Benchmark(Description = "RamDisk Read (uncached)")]
    public void RamDisk_SequentialReadUncached() =>
        UnbufferedIo.ReadFull(_ramFile, FileSizeBytes);

    [Benchmark(Description = "RamDisk Write")]
    public void RamDisk_SequentialWrite()
    {
        using var fs = new FileStream(_ramFile, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 4096, FileOptions.WriteThrough);
        fs.Write(_writeBuffer, 0, _writeBuffer.Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        var mountPoint = DriveLetterHelper.FindFreeMountPoint();
        _ramDisk = RamDisk.Create(new()
        {
            CapacityBytes = CapacityBytes,
            MountPoint = mountPoint,
            VolumeLabel = "BenchDisk",
        });
        _ramFile = Path.Combine(mountPoint + @"\", "bench.dat");

        _tempDir = Path.Combine(Path.GetTempPath(), $"ManagedDriveBench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempFile = Path.Combine(_tempDir, "bench.dat");

        _writeBuffer = new byte[FileSizeBytes];
        new Random(42).NextBytes(_writeBuffer);
        _readBuffer = new byte[FileSizeBytes];

        // Pre-create files so read benchmarks can run in isolation
        File.WriteAllBytes(_ramFile, _writeBuffer);
        File.WriteAllBytes(_tempFile, _writeBuffer);
    }
}