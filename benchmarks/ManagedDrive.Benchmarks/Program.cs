using BenchmarkDotNet.Running;
using ManagedDrive.Benchmarks;

BenchmarkSwitcher.FromTypes([
    typeof(SequentialReadWriteBenchmarks),
    typeof(RandomAccessBenchmarks),
    typeof(ConcurrentAccessBenchmarks)
]).Run(args);