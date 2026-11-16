using ManagedDrive.App.Infrastructure;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="SourcePathParts"/>: how a source path is split for the card.
/// </summary>
public sealed class SourcePathPartsTests
{
    /// <summary>
    /// Verifies that a full path gives its file name and folder.
    /// </summary>
    [Fact]
    public void Split_FullPath_ReturnsNameAndDirectory()
    {
        var (name, directory) = SourcePathParts.Split(@"D:\Downloads\dataset.zip");

        Assert.Equal("dataset.zip", name);
        Assert.Equal(@"D:\Downloads", directory);
    }

    /// <summary>
    /// Verifies that a bare file name has no folder.
    /// </summary>
    [Fact]
    public void Split_FileNameOnly_HasEmptyDirectory()
    {
        var (name, directory) = SourcePathParts.Split("dataset.zip");

        Assert.Equal("dataset.zip", name);
        Assert.Equal(string.Empty, directory);
    }

    /// <summary>
    /// Verifies that a trailing separator is ignored.
    /// </summary>
    [Fact]
    public void Split_TrailingSeparator_IsIgnored()
    {
        var (name, directory) = SourcePathParts.Split(@"D:\Downloads\dataset.zip\");

        Assert.Equal("dataset.zip", name);
        Assert.Equal(@"D:\Downloads", directory);
    }

    /// <summary>
    /// Verifies that a missing or blank path gives empty parts.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Split_MissingPath_ReturnsEmptyParts(string? path)
    {
        var (name, directory) = SourcePathParts.Split(path);

        Assert.Equal(string.Empty, name);
        Assert.Equal(string.Empty, directory);
    }
}
