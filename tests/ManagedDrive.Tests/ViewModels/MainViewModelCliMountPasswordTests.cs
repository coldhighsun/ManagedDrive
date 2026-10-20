using ManagedDrive.App.Localization;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for how the CLI mount treats a password supplied for an image that is not encrypted.
/// </summary>
public sealed class MainViewModelCliMountPasswordTests : ViewModelTestBase
{
    /// <summary>
    /// Writes an image with an empty root directory, optionally encrypted.
    /// </summary>
    /// <param name="encryption">The encryption to apply, or <c>null</c> for a plain image.</param>
    /// <returns>The image path.</returns>
    private string WriteImage(ImageEncryptionInfo? encryption)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, "disk.mdr");
        var map = new FileNodeMap();
        map.Add("\\", new() { FileInfo = { FileAttributes = (uint)FileAttributes.Directory } });
        DiskImageSerializer.Save(map, capacityBytes: 1024 * 1024, "Label", path, Core.Mounting.ImageCompressionLevel.None, encryption);
        return path;
    }

    /// <summary>
    /// Mounting an unencrypted image with a password is refused before anything is mounted, so the
    /// image is not turned into an encrypted one and its snapshots are not deleted.
    /// </summary>
    [Fact]
    public async Task MountImageAsync_PasswordForUnencryptedImage_IsRefusedAndImageIsUnchanged()
    {
        var imagePath = WriteImage(encryption: null);
        var before = await File.ReadAllBytesAsync(imagePath, TestContext.Current.CancellationToken);
        var viewModel = CreateViewModel(CreateStore());

        var (success, message) = await viewModel.MountImageAsync(imagePath, "R:", new() { Password = "correct horse" });

        Assert.False(success);
        Assert.Equal(Loc.Get("Val.CliPasswordNotApplicable"), message);
        Assert.Empty(viewModel.Disks);
        Assert.Equal(before, await File.ReadAllBytesAsync(imagePath, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An encrypted image without a password still gets the existing "password required" refusal,
    /// not the "not applicable" one.
    /// </summary>
    [Fact]
    public async Task MountImageAsync_EncryptedImageWithoutPassword_IsRefusedAsPasswordRequired()
    {
        var imagePath = WriteImage(new ImageEncryptionInfo("s3cretpass", DiskImageSerializer.GenerateCek()));
        var viewModel = CreateViewModel(CreateStore());

        var (success, message) = await viewModel.MountImageAsync(imagePath, "R:", new());

        Assert.False(success);
        Assert.Equal(Loc.Get("Val.CliPasswordRequired"), message);
    }
}
