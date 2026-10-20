using ManagedDrive.App.Localization;
using ManagedDrive.App.ViewModels;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests that the CLI applies the dialogs' password length rule to passwords it encrypts with.
/// </summary>
public sealed class MainViewModelCliPasswordLengthTests : ViewModelTestBase
{
    /// <summary>
    /// No password at all (no encryption requested) is not an error.
    /// </summary>
    [Fact]
    public void GetNewPasswordError_NoPassword_ReturnsNull()
    {
        Assert.Null(MainViewModel.GetNewPasswordError(null));
    }

    /// <summary>
    /// A password of an accepted length is not an error.
    /// </summary>
    [Fact]
    public void GetNewPasswordError_PasswordWithinLimits_ReturnsNull()
    {
        Assert.Null(MainViewModel.GetNewPasswordError(new string('a', CreateDiskOptionsBuilder.MinPasswordLength)));
    }

    /// <summary>
    /// An empty password (what an empty <c>--password-file</c> yields) reports the "too short" message.
    /// </summary>
    [Fact]
    public void GetNewPasswordError_EmptyPassword_ReturnsTooShortMessage()
    {
        var error = MainViewModel.GetNewPasswordError(string.Empty);

        Assert.Equal(Loc.Format("Val.PasswordTooShort", CreateDiskOptionsBuilder.MinPasswordLength), error);
    }

    /// <summary>
    /// An oversized password reports the "too long" message.
    /// </summary>
    [Fact]
    public void GetNewPasswordError_OversizedPassword_ReturnsTooLongMessage()
    {
        var error = MainViewModel.GetNewPasswordError(new string('a', CreateDiskOptionsBuilder.MaxPasswordLength + 1));

        Assert.Equal(Loc.Format("Val.PasswordTooLong", CreateDiskOptionsBuilder.MaxPasswordLength), error);
    }

    /// <summary>
    /// <c>create</c> with a password outside the limits is refused before any disk is mounted, with
    /// the length message.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public async Task CreateByOptionsAsync_PasswordTooShort_IsRefusedWithoutMounting(string password)
    {
        var viewModel = CreateViewModel(CreateStore());

        var (success, message) = await viewModel.CreateByOptionsAsync(
            "R:", 64UL * 1024 * 1024, volumeLabel: null, Path.Combine(Dir, "r.mdr"), password);

        Assert.False(success);
        Assert.Equal(Loc.Format("Val.PasswordTooShort", CreateDiskOptionsBuilder.MinPasswordLength), message);
        Assert.Empty(viewModel.Disks);
        Assert.False(File.Exists(Path.Combine(Dir, "r.mdr")));
    }

    /// <summary>
    /// <c>create</c> with a password above the maximum is refused with the "too long" message.
    /// </summary>
    [Fact]
    public async Task CreateByOptionsAsync_PasswordTooLong_IsRefusedWithoutMounting()
    {
        var viewModel = CreateViewModel(CreateStore());

        var (success, message) = await viewModel.CreateByOptionsAsync(
            "R:", 64UL * 1024 * 1024, volumeLabel: null, Path.Combine(Dir, "r.mdr"),
            new string('a', CreateDiskOptionsBuilder.MaxPasswordLength + 1));

        Assert.False(success);
        Assert.Equal(Loc.Format("Val.PasswordTooLong", CreateDiskOptionsBuilder.MaxPasswordLength), message);
        Assert.Empty(viewModel.Disks);
    }
}
