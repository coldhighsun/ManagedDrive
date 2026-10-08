using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="FileSizeProbe"/>, whose contract is to never throw for a bad path.
/// </summary>
public sealed class FileSizeProbeTests
{
    /// <summary>
    /// An existing file reports its length.
    /// </summary>
    [Fact]
    public void TryGetSize_ExistingFile_ReturnsLength()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);

            Assert.Equal(3UL, FileSizeProbe.TryGetSize(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A path that is null, empty or malformed gives null instead of throwing.
    /// </summary>
    /// <param name="path">The path to probe.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad\0path.mdr")]
    public void TryGetSize_NullOrMalformedPath_ReturnsNull(string? path)
    {
        Assert.Null(FileSizeProbe.TryGetSize(path));
    }

    /// <summary>
    /// A file that doesn't exist gives null. The path is under the temp directory so no drive
    /// letter that might be an offline network mapping is involved.
    /// </summary>
    [Fact]
    public void TryGetSize_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mdr");

        Assert.Null(FileSizeProbe.TryGetSize(path));
    }
}
