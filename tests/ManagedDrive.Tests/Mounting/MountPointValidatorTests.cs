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
        Assert.True(MountPointValidator.TryValidateDirectoryMountPoint("R:", [], out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateDirectoryMountPoint_DriveLetter_IgnoresNestingCheck()
    {
        // A drive letter can never be "inside" another mount point, so it should short-circuit
        // before the nesting check even if (nonsensically) passed one of itself.
        Assert.True(MountPointValidator.TryValidateDirectoryMountPoint("R:", ["R:"], out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateDirectoryMountPoint_ExistingEmptyDirectory_ReturnsTrue()
    {
        Directory.CreateDirectory(_dir);

        Assert.True(MountPointValidator.TryValidateDirectoryMountPoint(_dir, [], out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateDirectoryMountPoint_NonExistentDirectory_ReturnsFalse()
    {
        Assert.False(MountPointValidator.TryValidateDirectoryMountPoint(_dir, [], out var error));
        Assert.Contains(_dir, error);
    }

    [Fact]
    public void TryValidateDirectoryMountPoint_NonEmptyDirectory_ReturnsFalse()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "existing.txt"), "content");

        Assert.False(MountPointValidator.TryValidateDirectoryMountPoint(_dir, [], out var error));
        Assert.Contains(_dir, error);
    }

    [Fact]
    public void TryValidateDirectoryMountPoint_NestedUnderDriveLetterMount_ReturnsFalse()
    {
        var nested = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(nested);

        // _dir itself isn't on a real R: drive, but the prefix check is purely textual — it
        // exercises the "target path starts with another mount point's path" logic without
        // needing an actual mounted disk.
        var fakeOuterMount = _dir[..1] + ":";
        Assert.False(MountPointValidator.TryValidateDirectoryMountPoint(nested, [fakeOuterMount], out var error));
        Assert.Contains(fakeOuterMount, error);
    }

    [Fact]
    public void TryValidateDirectoryMountPoint_NestedUnderOtherDirectoryMount_ReturnsFalse()
    {
        var outer = Path.Combine(_dir, "outer");
        var inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);

        Assert.False(MountPointValidator.TryValidateDirectoryMountPoint(inner, [outer], out var error));
        Assert.Contains(outer, error);
    }

    [Fact]
    public void TryValidateDirectoryMountPoint_SiblingOfOtherDirectoryMount_ReturnsTrue()
    {
        var sibling1 = Path.Combine(_dir, "sibling1");
        var sibling2 = Path.Combine(_dir, "sibling2");
        Directory.CreateDirectory(sibling1);
        Directory.CreateDirectory(sibling2);

        Assert.True(MountPointValidator.TryValidateDirectoryMountPoint(sibling2, [sibling1], out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(@"R:\file.mdr", "R:")]
    [InlineData(@"r:\dir\file.mdr", "R:")]
    [InlineData(@"R:", "R:")]
    [InlineData(@"C:\ram\file.mdr", @"C:\ram")]
    [InlineData(@"C:\ram", @"C:\ram\")]
    [InlineData(@"C:/ram/file.mdr", @"C:\ram")]
    [InlineData(@"C:\other\..\ram\file.mdr", @"C:\ram")]
    public void IsPathOnMountPoint_PathInsideMountPoint_ReturnsTrue(string path, string mountPoint)
    {
        Assert.True(MountPointValidator.IsPathOnMountPoint(path, mountPoint));
    }

    [Theory]
    [InlineData(@"C:\ram2\file.mdr", @"C:\ram")]
    [InlineData(@"C:\ram\..\file.mdr", @"C:\ram")]
    [InlineData(@"S:\file.mdr", "R:")]
    [InlineData(@"C:\ram", @"C:\ram\sub")]
    [InlineData("", "R:")]
    [InlineData("C:\\bad\0name.mdr", "C:")]
    public void IsPathOnMountPoint_PathOutsideMountPointOrMalformed_ReturnsFalse(string path, string mountPoint)
    {
        Assert.False(MountPointValidator.IsPathOnMountPoint(path, mountPoint));
    }

    /// <summary>
    /// Drive letters and directory mount points normalize to a canonical absolute path ending
    /// in a backslash.
    /// </summary>
    /// <param name="mountPoint">The mount point.</param>
    /// <param name="expected">The expected normalized form.</param>
    [Theory]
    [InlineData("R:", @"R:\")]
    [InlineData(@"C:\ram", @"C:\ram\")]
    [InlineData(@"C:\ram\", @"C:\ram\")]
    [InlineData(@"C:/ram", @"C:\ram\")]
    [InlineData(@"C:\other\..\ram", @"C:\ram\")]
    public void NormalizeForPrefixCheck_MountPoint_ReturnsCanonicalPathWithTrailingBackslash(string mountPoint, string expected)
    {
        var normalized = MountPointValidator.NormalizeForPrefixCheck(mountPoint);

        Assert.Equal(expected, normalized);
    }
}
