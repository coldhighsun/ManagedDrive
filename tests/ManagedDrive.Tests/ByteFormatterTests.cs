using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public sealed class ByteFormatterTests
{
    [Theory]
    [InlineData(0.0, "0 B/s")]
    [InlineData(0.5, "0 B/s")]
    [InlineData(512.0, "512 B/s")]
    [InlineData(1024.0 * 5, "5.0 KB/s")]
    [InlineData(1024.0 * 1024 * 3, "3.0 MB/s")]
    [InlineData(1024.0 * 1024 * 1024 * 2, "2.0 GB/s")]
    public void FormatRate_UsesSameUnitThresholdsAsFormat(double bytesPerSecond, string expected)
    {
        var formatted = ByteFormatter.FormatRate(bytesPerSecond);

        Assert.Equal(expected, formatted);
    }
}
