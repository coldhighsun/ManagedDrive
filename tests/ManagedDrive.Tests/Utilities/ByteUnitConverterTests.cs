namespace ManagedDrive.Tests;

public sealed class ByteUnitConverterTests
{
    [Theory]
    [InlineData(2UL * 1024 * 1024 * 1024, 2, true)]   // 2 GB -> GB
    [InlineData(512UL * 1024 * 1024, 512, false)]      // 512 MB -> MB (not whole GB)
    [InlineData(1536UL * 1024 * 1024, 1536, false)]    // 1.5 GB -> MB (not whole GB)
    [InlineData(1024UL * 1024, 1, false)]              // 1 MB -> MB
    [InlineData(0UL, 0, false)]                          // 0 bytes -> 0 MB
    public void SplitToUnit_PrefersWholeGb(ulong bytes, int expectedValue, bool expectedIsGb)
    {
        var (value, isGb) = ByteUnitConverter.SplitToUnit(bytes);

        Assert.Equal(expectedValue, value);
        Assert.Equal(expectedIsGb, isGb);
    }

    [Theory]
    [InlineData(2, true, 2UL * 1024 * 1024 * 1024)]
    [InlineData(512, false, 512UL * 1024 * 1024)]
    public void ToBytes_RoundTripsWithSplitToUnit(int value, bool isGb, ulong expectedBytes)
    {
        var bytes = ByteUnitConverter.ToBytes(value, isGb);

        Assert.Equal(expectedBytes, bytes);
        var (rtValue, rtIsGb) = ByteUnitConverter.SplitToUnit(bytes);
        Assert.Equal(value, rtValue);
        Assert.Equal(isGb, rtIsGb);
    }

    [Fact]
    public void MaxValueForUnit_ClampsToAtLeastOne()
    {
        Assert.Equal(1, ByteUnitConverter.MaxValueForUnit(0, isGb: true));
        Assert.Equal(4, ByteUnitConverter.MaxValueForUnit(4UL * 1024 * 1024 * 1024, isGb: true));
        Assert.Equal(4096, ByteUnitConverter.MaxValueForUnit(4UL * 1024 * 1024 * 1024, isGb: false));
    }
}
