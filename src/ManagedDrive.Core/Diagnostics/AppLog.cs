using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedDrive.Core.Diagnostics;

/// <summary>
/// Thin static logging entry point for Core types that cannot take a constructor-injected
/// <see cref="ILogger{T}"/> without breaking existing public API (the static
/// <see cref="Snapshots.SnapshotManager"/> and the widely-called <see cref="Mounting.RamDisk.Create"/>
/// factory). The App layer calls <see cref="Configure"/> once at startup with a concrete
/// <see cref="ILoggerFactory"/> (backed by Serilog); until then loggers are no-ops.
/// </summary>
public static class AppLog
{
    /// <summary>
    /// The currently installed logger factory, swapped in by <see cref="Configure"/>. No-op until
    /// then.
    /// </summary>
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    /// <summary>
    /// Installs the concrete <see cref="ILoggerFactory"/> the App layer configures at startup.
    /// Loggers previously handed out by <see cref="CreateLogger{T}"/>/<see cref="CreateLogger(Type)"/>
    /// pick this up automatically on their next call — they resolve against the current factory
    /// lazily rather than capturing the one in effect when they were created, since several Core
    /// callers cache their logger in a <c>static readonly</c> field that is initialized the first
    /// time the type is touched, which can happen before <see cref="Configure"/> runs.
    /// </summary>
    public static void Configure(ILoggerFactory factory) => Volatile.Write(ref _factory, factory);

    /// <summary>
    /// Returns a logger for <typeparamref name="T"/> that always logs through whichever
    /// <see cref="ILoggerFactory"/> is current at the time of each call, not the one current when
    /// this method was called.
    /// </summary>
    public static ILogger<T> CreateLogger<T>() => new DeferredLogger<T>();

    /// <summary>
    /// Returns a logger for <paramref name="type"/> that always logs through whichever
    /// <see cref="ILoggerFactory"/> is current at the time of each call, not the one current when
    /// this method was called.
    /// </summary>
    public static ILogger CreateLogger(Type type) => new DeferredLogger(type);

    /// <summary>
    /// <see cref="ILogger"/> that resolves the concrete logger from <see cref="_factory"/> on
    /// every call instead of once at construction time.
    /// </summary>
    private class DeferredLogger : ILogger
    {
        /// <summary>
        /// The category type this logger reports as, passed to <see cref="ILoggerFactory.CreateLogger(string)"/>
        /// (via the <c>Type</c> overload) the first time it's needed after <see cref="_factory"/> changes.
        /// </summary>
        private readonly Type _type;

        /// <summary>
        /// The most recently resolved (factory, logger) pair, reused by <see cref="Resolve"/> as
        /// long as <see cref="_factory"/> hasn't changed since — so a single call (or a natural
        /// pairing like <see cref="IsEnabled"/> followed immediately by <see cref="Log"/>) sees a
        /// consistent logger instead of each independently re-resolving <see cref="_factory"/> and
        /// risking a torn view if <see cref="Configure"/> runs in between. Assigned as a whole
        /// object reference so a concurrent reader never observes a mismatched factory/logger pair.
        /// </summary>
        private ResolvedLogger? _resolved;

        /// <summary>
        /// Initializes a new deferred logger for the given category <paramref name="type"/>.
        /// </summary>
        /// <param name="type">The category type to log under.</param>
        internal DeferredLogger(Type type) => _type = type;

        /// <summary>
        /// Begins a logging scope on the resolved logger for <see cref="_type"/>.
        /// </summary>
        /// <typeparam name="TState">The type of the scope state.</typeparam>
        /// <param name="state">The scope state.</param>
        /// <returns>A disposable that ends the scope, or <see langword="null"/>.</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Resolve().BeginScope(state);

        /// <summary>
        /// Checks whether <paramref name="logLevel"/> is enabled on the resolved logger for
        /// <see cref="_type"/>.
        /// </summary>
        /// <param name="logLevel">The log level to check.</param>
        /// <returns><see langword="true"/> if the level is enabled; otherwise <see langword="false"/>.</returns>
        public bool IsEnabled(LogLevel logLevel) => Resolve().IsEnabled(logLevel);

        /// <summary>
        /// Writes a log entry through the resolved logger for <see cref="_type"/>.
        /// </summary>
        /// <typeparam name="TState">The type of the log entry's state.</typeparam>
        /// <param name="logLevel">The severity of the entry.</param>
        /// <param name="eventId">The event id associated with the entry.</param>
        /// <param name="state">The entry's state.</param>
        /// <param name="exception">The exception related to the entry, if any.</param>
        /// <param name="formatter">Formats the state and exception into a message string.</param>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Resolve().Log(logLevel, eventId, state, exception, formatter);

        /// <summary>
        /// Returns the logger for <see cref="_type"/> from the currently installed factory,
        /// reusing the cached one from <see cref="_resolved"/> when the factory is unchanged.
        /// </summary>
        private ILogger Resolve()
        {
            var factory = Volatile.Read(ref _factory);
            var cached = Volatile.Read(ref _resolved);
            if (cached is not null && ReferenceEquals(cached.Factory, factory))
            {
                return cached.Logger;
            }

            var resolved = new ResolvedLogger(factory, factory.CreateLogger(_type));
            Volatile.Write(ref _resolved, resolved);
            return resolved.Logger;
        }

        /// <summary>
        /// An immutable (factory, logger) pair cached by <see cref="Resolve"/>.
        /// </summary>
        /// <param name="Factory">The factory the logger was created from.</param>
        /// <param name="Logger">The logger created from <paramref name="Factory"/>.</param>
        private sealed record ResolvedLogger(ILoggerFactory Factory, ILogger Logger);
    }

    /// <summary>
    /// Typed counterpart of <see cref="DeferredLogger"/> for <see cref="ILogger{T}"/> callers.
    /// </summary>
    /// <typeparam name="T">The category type this logger reports as.</typeparam>
    private sealed class DeferredLogger<T> : DeferredLogger, ILogger<T>
    {
        /// <summary>
        /// Initializes a new deferred logger for category <typeparamref name="T"/>.
        /// </summary>
        internal DeferredLogger() : base(typeof(T))
        {
        }
    }
}
