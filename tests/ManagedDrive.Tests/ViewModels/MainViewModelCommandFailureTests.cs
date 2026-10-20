using ManagedDrive.App.Localization;
using ManagedDrive.App.ViewModels;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for how the view model reports an exception that escaped an asynchronous command.
/// </summary>
public sealed class MainViewModelCommandFailureTests : ViewModelTestBase
{
    /// <summary>
    /// A disposed disk means it was unmounted while the command ran: the status bar says so and no
    /// dialog is shown (a dialog would block this test).
    /// </summary>
    [Fact]
    public void OnCommandFailed_ObjectDisposedException_ShowsStatusTextOnly()
    {
        var viewModel = CreateViewModel(CreateStore());
        var before = viewModel.StatusText;

        viewModel.OnCommandFailed(new ObjectDisposedException("RamDisk"));

        Assert.Equal(Loc.Get("Status.CommandTargetGone"), viewModel.StatusText);
        Assert.NotEqual(before, viewModel.StatusText);
    }

    /// <summary>
    /// Both spellings of the disk's name that <c>RamDisk</c> uses when it throws after being
    /// disposed (<c>nameof</c> and the type's full name) count as the disk being gone.
    /// </summary>
    [Theory]
    [InlineData("RamDisk")]
    [InlineData("ManagedDrive.Core.Mounting.RamDisk")]
    public void IsDiskGone_DisposedRamDisk_ReturnsTrue(string objectName)
    {
        Assert.True(MainViewModel.IsDiskGone(new ObjectDisposedException(objectName)));
    }

    /// <summary>
    /// Anything else must stay visible as an error: another disposed object, a disposed object
    /// without a name, and exceptions that are not disposal at all.
    /// </summary>
    [Fact]
    public void IsDiskGone_OtherDisposedObjectOrOtherException_ReturnsFalse()
    {
        Assert.False(MainViewModel.IsDiskGone(new ObjectDisposedException("CancellationTokenSource")));
        Assert.False(MainViewModel.IsDiskGone(new ObjectDisposedException(objectName: null)));
        Assert.False(MainViewModel.IsDiskGone(new InvalidOperationException("RamDisk")));
    }
}
