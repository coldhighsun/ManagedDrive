using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="SplashTopmostState"/>, which decides whether the startup splash stays on top.
/// </summary>
public sealed class SplashTopmostStateTests
{
    /// <summary>
    /// A new splash is on top.
    /// </summary>
    [Fact]
    public void IsTopmost_Initially_IsTrue()
    {
        Assert.True(new SplashTopmostState().IsTopmost);
    }

    /// <summary>
    /// Releasing ends staying on top for good.
    /// </summary>
    [Fact]
    public void IsTopmost_AfterRelease_IsFalse()
    {
        var state = new SplashTopmostState();

        state.Release();

        Assert.False(state.IsTopmost);
    }

    /// <summary>
    /// Pausing suspends staying on top and resuming restores it.
    /// </summary>
    [Fact]
    public void IsTopmost_PausedThenResumed_FollowsPause()
    {
        var state = new SplashTopmostState();

        state.Pause(true);
        Assert.False(state.IsTopmost);

        state.Pause(false);
        Assert.True(state.IsTopmost);
    }

    /// <summary>
    /// Resuming after the splash was released does not put it back on top.
    /// </summary>
    [Fact]
    public void IsTopmost_ReleasedWhilePaused_StaysFalseAfterResume()
    {
        var state = new SplashTopmostState();
        state.Pause(true);
        state.Release();

        state.Pause(false);

        Assert.False(state.IsTopmost);
    }
}
