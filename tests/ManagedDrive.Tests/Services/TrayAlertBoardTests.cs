using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="TrayAlertRules"/> and <see cref="TrayAlertBoard"/>.
/// </summary>
public sealed class TrayAlertBoardTests
{
    /// <summary>
    /// Verifies the level each reason maps to, and that a critical reason wins over a warning.
    /// </summary>
    [Theory]
    [InlineData(TrayAlertReasons.None, TrayAlertLevel.None)]
    [InlineData(TrayAlertReasons.HighUsage, TrayAlertLevel.Warning)]
    [InlineData(TrayAlertReasons.LowMemory, TrayAlertLevel.Warning)]
    [InlineData(TrayAlertReasons.DiskFull, TrayAlertLevel.Critical)]
    [InlineData(TrayAlertReasons.MemoryRejected, TrayAlertLevel.Critical)]
    [InlineData(TrayAlertReasons.SaveFailed, TrayAlertLevel.Critical)]
    [InlineData(TrayAlertReasons.HighUsage | TrayAlertReasons.DiskFull, TrayAlertLevel.Critical)]
    public void GetLevel_Reasons_MapsToLevel(TrayAlertReasons reasons, TrayAlertLevel expected)
    {
        // Act
        var level = TrayAlertRules.GetLevel(reasons);

        // Assert
        Assert.Equal(expected, level);
    }

    /// <summary>
    /// Verifies that the board's level is the most urgent of all keys.
    /// </summary>
    [Fact]
    public void Level_SeveralKeys_IsTheMostUrgent()
    {
        // Arrange
        var board = new TrayAlertBoard();
        board.Set("R:", TrayAlertReasons.HighUsage, true);

        // Act
        board.Set("S:", TrayAlertReasons.SaveFailed, true);

        // Assert
        Assert.Equal(TrayAlertLevel.Critical, board.Level);
    }

    /// <summary>
    /// Verifies that clearing one reason keeps the others of the same key.
    /// </summary>
    [Fact]
    public void Set_ClearOneReason_KeepsTheOthers()
    {
        // Arrange
        var board = new TrayAlertBoard();
        board.Set("R:", TrayAlertReasons.HighUsage | TrayAlertReasons.DiskFull, true);

        // Act
        var changed = board.Set("R:", TrayAlertReasons.DiskFull, false);

        // Assert
        Assert.True(changed);
        Assert.Equal(TrayAlertReasons.HighUsage, board.Get("R:"));
        Assert.Equal(TrayAlertLevel.Warning, board.Level);
    }

    /// <summary>
    /// Verifies that setting a reason a key already has reports no change.
    /// </summary>
    [Fact]
    public void Set_SameReasonAgain_ReportsNoChange()
    {
        // Arrange
        var board = new TrayAlertBoard();
        board.Set("R:", TrayAlertReasons.DiskFull, true);

        // Act
        var changed = board.Set("R:", TrayAlertReasons.DiskFull, true);

        // Assert
        Assert.False(changed);
    }

    /// <summary>
    /// Verifies that removing a key clears its reasons and the level drops.
    /// </summary>
    [Fact]
    public void Remove_KeyWithReasons_LevelDrops()
    {
        // Arrange
        var board = new TrayAlertBoard();
        board.Set("R:", TrayAlertReasons.DiskFull, true);

        // Act
        var removed = board.Remove("R:");

        // Assert
        Assert.True(removed);
        Assert.Equal(TrayAlertReasons.None, board.Get("R:"));
        Assert.Equal(TrayAlertLevel.None, board.Level);
    }
}
