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

    [Theory]
    [InlineData("8", 2, 8)]
    [InlineData(" 16 ", 2, 16)]
    [InlineData("999", 2, 999)]                   // no upper limit applied
    [InlineData("9999999999", 2, int.MaxValue)]   // beyond int -> clamped to int.MaxValue
    [InlineData("0", 2, 1)]                       // below min -> clamped
    [InlineData("99999999999999999999", 2, 2)]    // long overflow -> fallback
    [InlineData("", 2, 2)]                        // empty -> fallback
    [InlineData("abc", 2, 2)]                     // not numeric -> fallback
    [InlineData("-5", 2, 2)]                      // sign not allowed -> fallback
    public void ParseCapacityInput_ReturnsParsedValueOrFallback(string text, int fallback, int expected)
    {
        var result = ByteUnitConverter.ParseCapacityInput(text, fallback);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseCapacityInput_NullText_ReturnsFallback()
    {
        Assert.Equal(3, ByteUnitConverter.ParseCapacityInput(null, 3));
    }

    [Theory]
    [InlineData(5, 1, 10, 6)]
    [InlineData(5, -1, 10, 4)]
    [InlineData(5, 10, 10, 10)]                      // above limit -> clamped
    [InlineData(5, -10, 10, 1)]                      // below minimum -> clamped
    [InlineData(1, -1, 10, 1)]
    [InlineData(50, -1, 10, 10)]                     // over-limit typed value steps back to the limit
    [InlineData(int.MaxValue, 1, int.MaxValue, int.MaxValue)]  // no overflow
    public void AddToCapacity_WithoutExistingValue_ClampsToRange(int current, int delta, int limit, int expected)
    {
        var result = ByteUnitConverter.AddToCapacity(current, delta, limit, existingValue: null);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(10, 1, 20)]       // up from the limit jumps to the existing capacity
    [InlineData(20, -1, 10)]      // down from the existing capacity jumps to the limit
    [InlineData(20, 1, 20)]       // already on the existing capacity
    [InlineData(8, 10, 10)]       // crossing the limit upwards stops on it first
    [InlineData(20, -10, 10)]     // no values in between
    [InlineData(10, -1, 9)]       // below the limit steps normally
    [InlineData(500, 1, 20)]      // over-limit typed value goes to the existing capacity
    [InlineData(500, -1, 10)]
    public void AddToCapacity_WithLargerExistingValue_SkipsForbiddenRange(int current, int delta, int expected)
    {
        var result = ByteUnitConverter.AddToCapacity(current, delta, normalMaximum: 10, existingValue: 20);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(10UL)]
    [InlineData(5UL)]
    public void AddToCapacity_ExistingValueWithinLimit_IsIgnored(ulong existing)
    {
        Assert.Equal(10, ByteUnitConverter.AddToCapacity(10, 1, 10, existing));
    }
}
