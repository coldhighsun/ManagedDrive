using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedDrive.Core.Diagnostics;

/// <summary>
/// Thin static logging entry point for Core types that cannot take a constructor-injected
/// <see cref="ILogger{T}"/> without breaking existing public API (the static
/// <see cref="Snapshots.SnapshotManager"/> and the widely-called <see cref="Mounting.RamDisk.Create"/>
/// factory). The App layer calls <see cref="Configure"/> once at startup with a concrete
/// <see cref="ILoggerFactory"/> (backed by Serilog); until then this returns a no-op logger.
/// </summary>
public static class AppLog
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    public static void Configure(ILoggerFactory factory) => _factory = factory;

    public static ILogger<T> CreateLogger<T>() => _factory.CreateLogger<T>();

    public static ILogger CreateLogger(Type type) => _factory.CreateLogger(type);
}