using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public sealed class AppInstanceTests : IDisposable
{
    public AppInstanceTests()
    {
        AppInstance.TestMutexNameOverride = $"ManagedDrive-Test-Instance-{Guid.NewGuid()}";
    }

    public void Dispose()
    {
        AppInstance.TestMutexNameOverride = null;
    }

    [Fact]
    public void IsRunning_NoMutex_ReturnsFalse()
    {
        Assert.False(AppInstance.IsRunning());
    }

    [Fact]
    public void IsRunning_MutexExists_ReturnsTrue()
    {
        using var mutex = new Mutex(false, AppInstance.TestMutexNameOverride!);

        Assert.True(AppInstance.IsRunning());
    }

    /// <summary>
    /// Verifies the first instance gets an owned mutex that <see cref="AppInstance.IsRunning"/> then sees.
    /// </summary>
    [Fact]
    public void TryAcquire_NoOtherInstance_ReturnsOwnedMutexThatMarksTheAppRunning()
    {
        var acquired = AppInstance.TryAcquire(out var mutex);

        try
        {
            Assert.True(acquired);
            Assert.NotNull(mutex);
            Assert.True(AppInstance.IsRunning());
        }
        finally
        {
            mutex?.ReleaseMutex();
            mutex?.Dispose();
        }
    }

    /// <summary>
    /// Verifies a second instance is refused while another process-level handle holds the mutex.
    /// </summary>
    [Fact]
    public void TryAcquire_AnotherInstanceHoldsTheMutex_ReturnsFalseWithNullMutex()
    {
        using var existing = new Mutex(false, AppInstance.TestMutexNameOverride!);

        var acquired = AppInstance.TryAcquire(out var mutex);

        Assert.False(acquired);
        Assert.Null(mutex);
    }

    /// <summary>
    /// Verifies the mutex can be taken again once the previous instance released and closed it.
    /// </summary>
    [Fact]
    public void TryAcquire_AfterTheOwnerReleasesTheMutex_Succeeds()
    {
        Assert.True(AppInstance.TryAcquire(out var first));
        first.ReleaseMutex();
        first.Dispose();

        var acquired = AppInstance.TryAcquire(out var second);

        try
        {
            Assert.True(acquired);
        }
        finally
        {
            second?.ReleaseMutex();
            second?.Dispose();
        }
    }
}
