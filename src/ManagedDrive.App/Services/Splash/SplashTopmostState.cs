namespace ManagedDrive.App.Services;

/// <summary>
/// Decides whether the startup splash stays on top of other windows. It does for the first
/// moments, so that it is seen at launch, but not for the whole of a long load (it would cover
/// the user's work), not while it would hide a dialog opened from the tray, and not while startup
/// shows message boxes of its own. Pure, so it can be unit tested.
/// </summary>
internal sealed class SplashTopmostState
{
    /// <summary>
    /// How long the splash stays on top after it has appeared.
    /// </summary>
    public static readonly TimeSpan StayOnTopFor = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether the splash has given up staying on top for good.
    /// </summary>
    private bool _released;

    /// <summary>
    /// Whether staying on top is suspended for now.
    /// </summary>
    private bool _paused;

    /// <summary>
    /// Gets a value indicating whether the splash should currently be on top of other windows.
    /// </summary>
    public bool IsTopmost => !_released && !_paused;

    /// <summary>
    /// Gives up staying on top for good (the time is up, or something else needs the screen).
    /// </summary>
    public void Release() => _released = true;

    /// <summary>
    /// Suspends or resumes staying on top, for something that has to appear above the splash
    /// for a while. Has no effect on a splash that has been released.
    /// </summary>
    /// <param name="paused">Whether staying on top is suspended.</param>
    public void Pause(bool paused) => _paused = paused;
}
