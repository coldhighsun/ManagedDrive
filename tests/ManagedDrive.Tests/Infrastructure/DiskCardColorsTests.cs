using ManagedDrive.App.Infrastructure;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="DiskCardColors"/>: the usage level that colours the usage bar and the accent
/// colour a drive letter gets.
/// </summary>
public sealed class DiskCardColorsTests
{
    /// <summary>
    /// Verifies that a disk below the critical share is normal without the high-usage warning and high with it.
    /// </summary>
    [Theory]
    [InlineData(10.0, false, UsageLevel.Normal)]
    [InlineData(80.0, false, UsageLevel.Normal)]
    [InlineData(80.0, true, UsageLevel.High)]
    [InlineData(94.9, true, UsageLevel.High)]
    public void Classify_BelowCritical_FollowsHighUsageFlag(double percent, bool isHigh, UsageLevel expected)
    {
        // Act
        var level = DiskCardColors.Classify(percent, isHigh);

        // Assert
        Assert.Equal(expected, level);
    }

    /// <summary>
    /// Verifies that a nearly full disk is critical whether or not the high-usage warning is on.
    /// </summary>
    [Theory]
    [InlineData(95.0, false)]
    [InlineData(95.0, true)]
    [InlineData(100.0, false)]
    public void Classify_AtOrAboveCriticalPercent_IsCritical(double percent, bool isHigh)
    {
        // Act
        var level = DiskCardColors.Classify(percent, isHigh);

        // Assert
        Assert.Equal(UsageLevel.Critical, level);
    }

    /// <summary>
    /// Verifies that the accent number is stable per drive letter, ignores case and the trailing part of the mount point.
    /// </summary>
    [Theory]
    [InlineData("R:", 1 + ('R' - 'A') % 6)]
    [InlineData("r:", 1 + ('R' - 'A') % 6)]
    [InlineData("R:\\", 1 + ('R' - 'A') % 6)]
    [InlineData("A:", 1)]
    [InlineData("B:", 2)]
    [InlineData("G:", 1)]
    public void AccentIndexFor_DriveLetter_IsStable(string mountPoint, int expected)
    {
        // Act
        var index = DiskCardColors.AccentIndexFor(mountPoint);

        // Assert
        Assert.Equal(expected, index);
    }

    /// <summary>
    /// Verifies that every letter maps into the range of the palette and that anything without a letter falls back to 1.
    /// </summary>
    [Fact]
    public void AccentIndexFor_AnyInput_StaysInPaletteRange()
    {
        // Arrange
        var inputs = Enumerable.Range('A', 26).Select(c => $"{(char)c}:")
            .Concat(new[] { null, "", "\\\\server\\share", "1:" });

        // Act & Assert
        foreach (var input in inputs)
        {
            var index = DiskCardColors.AccentIndexFor(input);
            Assert.InRange(index, 1, DiskCardColors.AccentCount);
        }

        Assert.Equal(1, DiskCardColors.AccentIndexFor(null));
        Assert.Equal(1, DiskCardColors.AccentIndexFor("\\\\server\\share"));
    }
}
