namespace ManagedDrive.Tests;

public sealed class FilePathComparisonTests
{
    [Theory]
    [InlineData(@"C:\disks\r.mdr", @"C:\disks\r.mdr")]
    [InlineData(@"C:\disks\r.mdr", @"c:\DISKS\R.MDR")]
    [InlineData(@"C:\disks\r.mdr", @"C:\other\..\disks\r.mdr")]
    public void AreSame_SameFile_IsTrue(string a, string b)
    {
        Assert.True(FilePathComparison.AreSame(a, b));
    }

    [Theory]
    [InlineData(@"C:\disks\r.mdr", @"C:\disks\s.mdr")]
    [InlineData(@"C:\disks\r.mdr", @"D:\disks\r.mdr")]
    public void AreSame_DifferentFiles_IsFalse(string a, string b)
    {
        Assert.False(FilePathComparison.AreSame(a, b));
    }

    [Theory]
    [InlineData(null, @"C:\disks\r.mdr")]
    [InlineData(@"C:\disks\r.mdr", null)]
    [InlineData(null, null)]
    public void AreSame_MissingPath_IsFalse(string? a, string? b)
    {
        Assert.False(FilePathComparison.AreSame(a, b));
    }

    [Fact]
    public void AreSame_RelativePath_IsComparedRaw_NotAgainstTheCurrentDirectory()
    {
        var absoluteInCurrentDirectory = Path.Combine(Directory.GetCurrentDirectory(), @"disks\r.mdr");

        // Would be true if the relative path were resolved against the working directory.
        Assert.False(FilePathComparison.AreSame(@"disks\r.mdr", absoluteInCurrentDirectory));
        Assert.True(FilePathComparison.AreSame(@"disks\r.mdr", @"DISKS\R.MDR"));
    }

    [Fact]
    public void AreSame_UnresolvablePath_FallsBackToTheRawStrings()
    {
        Assert.True(FilePathComparison.AreSame("bad\0path", "BAD\0PATH"));
        Assert.False(FilePathComparison.AreSame("bad\0path", "other\0path"));
        Assert.True(FilePathComparison.AreSame("C:\\bad\0path", "c:\\BAD\0PATH"));
    }
}
