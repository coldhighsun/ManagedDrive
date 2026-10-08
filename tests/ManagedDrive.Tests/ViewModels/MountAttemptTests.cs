using ManagedDrive.App.ViewModels;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="MainViewModel.MountAttempt"/>, the result of loading a saved disk.
/// </summary>
public sealed class MountAttemptTests
{
    /// <summary>
    /// A mount that neither produced a disk nor failed is waiting for a password.
    /// </summary>
    [Fact]
    public void NeedsPassword_NoDiskAndNoError_IsTrue()
    {
        var attempt = new MainViewModel.MountAttempt(null, null);

        Assert.True(attempt.NeedsPassword);
    }

    /// <summary>
    /// A failed mount is not waiting for a password.
    /// </summary>
    [Fact]
    public void NeedsPassword_WithError_IsFalse()
    {
        var attempt = new MainViewModel.MountAttempt(null, null, new IOException());

        Assert.False(attempt.NeedsPassword);
    }
}
