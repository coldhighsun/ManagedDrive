using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

/// <summary>
/// An event source the test raises events on by hand.
/// </summary>
internal sealed class FakeCliEventSource : ICliEventSource
{
    /// <summary>
    /// Upper bound on waiting for the subscriber count to settle.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The handlers of the current subscriptions.
    /// </summary>
    private readonly List<Action<CliEvent>> _handlers = [];

    /// <summary>
    /// Gets how many subscriptions are active.
    /// </summary>
    public int SubscriberCount
    {
        get
        {
            lock (_handlers)
            {
                return _handlers.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task<IDisposable> SubscribeAsync(Action<CliEvent> handler)
    {
        lock (_handlers)
        {
            _handlers.Add(handler);
        }

        return Task.FromResult<IDisposable>(new Subscription(this, handler));
    }

    /// <summary>
    /// Delivers <paramref name="cliEvent"/> to every active subscription.
    /// </summary>
    /// <param name="cliEvent">The event.</param>
    public void Raise(CliEvent cliEvent)
    {
        Action<CliEvent>[] handlers;
        lock (_handlers)
        {
            handlers = [.. _handlers];
        }

        foreach (var handler in handlers)
        {
            handler(cliEvent);
        }
    }

    /// <summary>
    /// Waits until exactly <paramref name="count"/> subscriptions are active.
    /// </summary>
    /// <param name="count">The expected number of subscriptions.</param>
    public async Task WaitForSubscribersAsync(int count)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (SubscriberCount != count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Expected {count} subscriber(s) but there are {SubscriberCount}.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// One subscription; disposing it removes the handler.
    /// </summary>
    /// <param name="source">The source it belongs to.</param>
    /// <param name="handler">The handler it added.</param>
    private sealed class Subscription(FakeCliEventSource source, Action<CliEvent> handler) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose()
        {
            lock (source._handlers)
            {
                source._handlers.Remove(handler);
            }
        }
    }
}
