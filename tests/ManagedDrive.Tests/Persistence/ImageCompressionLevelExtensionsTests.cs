namespace ManagedDrive.Tests;

public sealed class ImageCompressionLevelExtensionsTests
{
    [Theory]
    [InlineData(ImageCompressionLevel.Fastest, System.IO.Compression.CompressionLevel.Fastest)]
    [InlineData(ImageCompressionLevel.SmallestSize, System.IO.Compression.CompressionLevel.SmallestSize)]
    [InlineData(ImageCompressionLevel.Optimal, System.IO.Compression.CompressionLevel.Optimal)]
    [InlineData(ImageCompressionLevel.None, System.IO.Compression.CompressionLevel.Optimal)]
    public void ToDotNetCompressionLevel_MapsToExpectedLevel(ImageCompressionLevel level, System.IO.Compression.CompressionLevel expected)
    {
        Assert.Equal(expected, level.ToDotNetCompressionLevel());
    }

    [Theory]
    [InlineData(ImageCompressionLevel.Fastest, 1)]
    [InlineData(ImageCompressionLevel.SmallestSize, 19)]
    [InlineData(ImageCompressionLevel.Optimal, 3)]
    [InlineData(ImageCompressionLevel.None, 3)]
    public void ToZstdLevel_NoCustomLevel_MapsToPresetValue(ImageCompressionLevel level, int expected)
    {
        Assert.Equal(expected, level.ToZstdLevel());
    }

    [Theory]
    [InlineData(ImageCompressionLevel.Fastest)]
    [InlineData(ImageCompressionLevel.Optimal)]
    [InlineData(ImageCompressionLevel.SmallestSize)]
    [InlineData(ImageCompressionLevel.None)]
    public void ToZstdLevel_CustomLevelSet_OverridesPresetForEveryLevel(ImageCompressionLevel level)
    {
        Assert.Equal(11, level.ToZstdLevel(customLevel: 11));
    }
}
