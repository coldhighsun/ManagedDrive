namespace ManagedDrive.Tests;

public sealed class MountPointValidatorTests : IDisposable
{
    private readonly string _dir;

    public MountPointValidatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("R:")]
    [InlineData("z:")]
    public void IsDriveLetter_SingleLetterAndColon_ReturnsTrue(string mountPoint)
    {
        Assert.True(MountPointValidator.IsDriveLetter(mountPoint));
    }

    [Theory]
    [InlineData("R")]
    [InlineData("R:\\")]
    [InlineData(@"C:\Temp\MyDrive")]
    public void IsDriveLetter_NotExactlyLetterColon_ReturnsFalse(string mountPoint)
    {
        Assert.False(MountPointValidator.IsDriveLetter(mountPoint));
    }

    [Fact]
    public void TryValidateDirectoryMountPoint_DriveLetter_ReturnsTrueWithoutTouchingDisk()
    {
        Assert.True(MountPointValidator.TryValidateDirectoryMountPoint("R:", out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateDirectoryMountPoint_ExistingEmptyDirectory_ReturnsTrue()
    {
        Directory.CreateDirectory(_dir);

        Assert.True(MountPointValidator.TryValidateDirectoryMountPoint(_dir, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateDirectoryMountPoint_NonExistentDirectory_ReturnsFalse()
    {
        Assert.False(MountPointValidator.TryValidateDirectoryMountPoint(_dir, out var error));
        Assert.Contains(_dir, error);
    }

    [Fact]
    public void TryValidateDirectoryMountPoint_NonEmptyDirectory_ReturnsFalse()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "existing.txt"), "content");

        Assert.False(MountPointValidator.TryValidateDirectoryMountPoint(_dir, out var error));
        Assert.Contains(_dir, error);
    }
}
