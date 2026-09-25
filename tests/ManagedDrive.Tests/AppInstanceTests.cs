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
}
