namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// A temporary "always on top" state for a window: raised to lift the window above the windows that
/// cover it, released exactly once, whichever of several triggers (activation, a fallback timeout)
/// comes first.
/// </summary>
/// <param name="setTopmost">Applies the topmost state to the window.</param>
internal sealed class TransientTopmost(Action<bool> setTopmost)
{
    /// <summary>
    /// Gets a value indicating whether the window is currently held topmost.
    /// </summary>
    public bool IsRaised { get; private set; }

    /// <summary>
    /// Makes the window topmost. Does nothing if it already is.
    /// </summary>
    public void Raise()
    {
        if (IsRaised)
        {
            return;
        }

        IsRaised = true;
        setTopmost(true);
    }

    /// <summary>
    /// Takes the topmost state away again. Safe to call from several triggers: only the first call
    /// after <see cref="Raise"/> has an effect.
    /// </summary>
    public void Release()
    {
        if (!IsRaised)
        {
            return;
        }

        IsRaised = false;
        setTopmost(false);
    }
}
