using BenchmarkDotNet.Attributes;

namespace ManagedDrive.Benchmarks;

[SimpleJob(warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
[MinColumn, MaxColumn]
public class RandomAccessBenchmarks
{
    private const int BlockBytes = 4 * 1024;
    private const ulong CapacityBytes = 128 * 1024 * 1024;
    private const int RandomReadCount = 30;
    private const int SmallFileCount = 30;
    private const int SourceFileBytes = 16 * 1024 * 1024;
    private string _mountPoint = null!;
    private RamDisk _ramDisk = null!;
    private string _ramSmallFileDir = null!;
    private string _ramSourceFile = null!;
    private byte[] _readBuffer = null!;
    private long[] _readOffsets = null!;
    private long[] _alignedReadOffsets = null!;
    private string _tempDir = null!;
    private string _tempSmallFileDir = null!;
    private string _tempSourceFile = null!;
    private byte[] _writeBuffer = null!;

    [GlobalCleanup]
    public void Cleanup()
    {
        _ramDisk.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [IterationSetup(Targets = [nameof(PhysicalDisk_SmallFileHighFrequencyWrite)])]
    public void IterationSetup_PhysicalSmallFiles()
    {
        if (Directory.Exists(_tempSmallFileDir))
            Directory.Delete(_tempSmallFileDir, recursive: true);
        Directory.CreateDirectory(_tempSmallFileDir);
    }

    [IterationSetup(Targets = [nameof(RamDisk_SmallFileHighFrequencyWrite)])]
    public void IterationSetup_RamSmallFiles()
    {
        if (Directory.Exists(_ramSmallFileDir))
            Directory.Delete(_ramSmallFileDir, recursive: true);
        Directory.CreateDirectory(_ramSmallFileDir);
    }

    // Reads a 16 MB source file written in Setup(); at this size the whole file typically stays in the
    // Windows page cache, so this measures OS-cache-hit random reads, not physical SSD seeks. See the
    // "(uncached)" variant for real medium performance.
    [Benchmark(Description = "PhysicalDisk RandomRead (OS cache)")]
    public void PhysicalDisk_RandomRead()
    {
        using var fs = new FileStream(_tempSourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var offset in _readOffsets)
        {
            fs.Seek(offset, SeekOrigin.Begin);
            fs.ReadExactly(_readBuffer, 0, _readBuffer.Length);
        }
    }

    // Bypasses the OS page cache so each random block read hits the SSD — the honest comparison against
    // the RAM disk, whose random reads always pay the WinFsp user-mode round-trip.
    [Benchmark(Description = "PhysicalDisk RandomRead (uncached)")]
    public void PhysicalDisk_RandomReadUncached() =>
        UnbufferedIo.ReadBlocksAt(_tempSourceFile, _alignedReadOffsets, BlockBytes);

    [Benchmark(Description = "PhysicalDisk SmallFileHighFrequencyWrite", Baseline = true)]
    public void PhysicalDisk_SmallFileHighFrequencyWrite()
    {
        for (var i = 0; i < SmallFileCount; i++)
        {
            var path = Path.Combine(_tempSmallFileDir, $"file-{i}.dat");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.Write(_writeBuffer, 0, _writeBuffer.Length);
        }
    }

    [Benchmark(Description = "RamDisk RandomRead (OS cache)")]
    public void RamDisk_RandomRead()
    {
        using var fs = new FileStream(_ramSourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var offset in _readOffsets)
        {
            fs.Seek(offset, SeekOrigin.Begin);
            fs.ReadExactly(_readBuffer, 0, _readBuffer.Length);
        }
    }

    // Forces each random block read through WinFsp (no OS cache), matching "PhysicalDisk RandomRead (uncached)".
    [Benchmark(Description = "RamDisk RandomRead (uncached)")]
    public void RamDisk_RandomReadUncached() =>
        UnbufferedIo.ReadBlocksAt(_ramSourceFile, _alignedReadOffsets, BlockBytes);

    [Benchmark(Description = "RamDisk SmallFileHighFrequencyWrite")]
    public void RamDisk_SmallFileHighFrequencyWrite()
    {
        for (var i = 0; i < SmallFileCount; i++)
        {
            var path = Path.Combine(_ramSmallFileDir, $"file-{i}.dat");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
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
            VolumeLabel = "BenchDisk",
        });

        _tempDir = Path.Combine(Path.GetTempPath(), $"ManagedDriveBench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var sourceData = new byte[SourceFileBytes];
        new Random(42).NextBytes(sourceData);

        _ramSourceFile = Path.Combine(_mountPoint + @"\", "random-source.dat");
        _tempSourceFile = Path.Combine(_tempDir, "random-source.dat");
        File.WriteAllBytes(_ramSourceFile, sourceData);
        File.WriteAllBytes(_tempSourceFile, sourceData);

        var rng = new Random(1234);
        _readOffsets = new long[RandomReadCount];
        _alignedReadOffsets = new long[RandomReadCount];
        for (var i = 0; i < RandomReadCount; i++)
        {
            _readOffsets[i] = rng.NextInt64(0, SourceFileBytes - BlockBytes);
            // NO_BUFFERING requires sector-aligned offsets; round each offset down to a BlockBytes
            // (4 KB) boundary for the uncached variants.
            _alignedReadOffsets[i] = _readOffsets[i] & ~((long)BlockBytes - 1);
        }

        _readBuffer = new byte[BlockBytes];
        _writeBuffer = new byte[BlockBytes];
        new Random(99).NextBytes(_writeBuffer);

        _ramSmallFileDir = Path.Combine(_mountPoint + @"\", "small-files-ram");
        _tempSmallFileDir = Path.Combine(_tempDir, "small-files-temp");
    }
}