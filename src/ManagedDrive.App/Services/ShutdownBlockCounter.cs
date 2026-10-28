namespace ManagedDrive.App.Services;

/// <summary>
/// Reference-counts a shutdown block reason. Windows keeps one reason per window, not one per
/// registration, so overlapping holders (a save that outlasted its call, and the next call's save)
/// must share it: it is registered when the first holder arrives and cleared only when the last
/// one is gone.
/// </summary>
/// <param name="register">Registers the reason; returns whether it is now registered.</param>
/// <param name="clear">Clears the reason.</param>
public sealed class ShutdownBlockCounter(Func<bool> register, Action clear)
{
    /// <summary>
    /// Guards <see cref="_holders"/> and <see cref="_registered"/>.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// How many holders are alive.
    /// </summary>
    private int _holders;

    /// <summary>
    /// Whether the reason is currently registered with the system.
    /// </summary>
    private bool _registered;

    /// <summary>
    /// Adds a holder, registering the reason if it is the first one or if an earlier attempt failed
    /// (the window handle may not have existed yet), so a later holder is not left unprotected by a
    /// failure that only hit the first.
    /// </summary>
    /// <returns>A scope that removes the holder when disposed; disposing twice is harmless.</returns>
    public IDisposable Acquire()
    {
        lock (_lock)
        {
            // Registered before counting: if it throws, no holder exists to be released.
            if (!_registered)
            {
                _registered = register();
            }

            _holders++;
        }

        return new Holder(this);
    }

    /// <summary>
    /// Removes a holder, clearing the reason when it was the last one.
    /// </summary>
    private void Release()
    {
        lock (_lock)
        {
            if (--_holders == 0 && _registered)
            {
                _registered = false;
                clear();
            }
        }
    }

    /// <summary>
    /// One holder of the block reason.
    /// </summary>
    /// <param name="owner">The counter the holder belongs to.</param>
    private sealed class Holder(ShutdownBlockCounter owner) : IDisposable
    {
        /// <summary>
        /// 1 once released.
        /// </summary>
        private int _released;

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
