namespace ManagedDrive.Tests;

public sealed class ZstdLevelsTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(22, true)]
    [InlineData(-1, false)]
    [InlineData(23, false)]
    public void IsValid_ReturnsExpected(int level, bool expected)
    {
        Assert.Equal(expected, ZstdLevels.IsValid(level));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(23)]
    public void ThrowIfInvalid_OutOfRange_Throws(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ZstdLevels.ThrowIfInvalid(level));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(22)]
    public void ThrowIfInvalid_InRange_DoesNotThrow(int level)
    {
        ZstdLevels.ThrowIfInvalid(level);
    }

    [Theory]
    [InlineData(0, null, 0)]
    [InlineData(1, null, 1)]
    [InlineData(2, null, 3)]
    [InlineData(3, null, 19)]
    [InlineData(2, 12, 12)]
    [InlineData(3, 40, 1)]
    [InlineData(0, 12, 0)]
    [InlineData(0, 99, 0)]
    public void FromLegacy_MapsOldPresetAndCustomLevel(int preset, int? custom, int expected)
    {
        Assert.Equal(expected, ZstdLevels.FromLegacy(preset, custom));
    }

    [Theory]
    [InlineData(false, 22)]
    [InlineData(true, 9)]
    public void MaxFor_ReturnsRangeOfTheExportKind(bool isArchive, int expected)
    {
        Assert.Equal(expected, ZstdLevels.MaxFor(isArchive));
    }
}
