namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="TransientTopmost"/>.
/// </summary>
public sealed class TransientTopmostTests
{
    /// <summary>
    /// Raising applies the topmost state.
    /// </summary>
    [Fact]
    public void Raise_NotRaised_SetsTopmost()
    {
        var calls = new List<bool>();
        var topmost = new TransientTopmost(calls.Add);

        topmost.Raise();

        Assert.Equal([true], calls);
        Assert.True(topmost.IsRaised);
    }

    /// <summary>
    /// Releasing after a raise removes the topmost state.
    /// </summary>
    [Fact]
    public void Release_Raised_ClearsTopmost()
    {
        var calls = new List<bool>();
        var topmost = new TransientTopmost(calls.Add);
        topmost.Raise();

        topmost.Release();

        Assert.Equal([true, false], calls);
        Assert.False(topmost.IsRaised);
    }

    /// <summary>
    /// Activation and the fallback timer may both release; the window is only touched once.
    /// </summary>
    [Fact]
    public void Release_CalledTwice_ClearsTopmostOnce()
    {
        var calls = new List<bool>();
        var topmost = new TransientTopmost(calls.Add);
        topmost.Raise();

        topmost.Release();
        topmost.Release();

        Assert.Equal([true, false], calls);
    }

    /// <summary>
    /// Releasing without a raise must not touch the window (a late timer tick after close).
    /// </summary>
    [Fact]
    public void Release_NeverRaised_DoesNothing()
    {
        var calls = new List<bool>();
        var topmost = new TransientTopmost(calls.Add);

        topmost.Release();

        Assert.Empty(calls);
    }

    /// <summary>
    /// Raising twice sets the state only once.
    /// </summary>
    [Fact]
    public void Raise_CalledTwice_SetsTopmostOnce()
    {
        var calls = new List<bool>();
        var topmost = new TransientTopmost(calls.Add);

        topmost.Raise();
        topmost.Raise();

        Assert.Equal([true], calls);
    }
}
