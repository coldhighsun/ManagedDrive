using BenchmarkDotNet.Running;
using ManagedDrive.Benchmarks;

BenchmarkRunner.Run<ReadWriteBenchmarks>(args: args);
