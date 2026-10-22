using ManagedDrive.App.Themes;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="TrayColorTable"/>, the palette of the tray icon's context menu.
/// </summary>
public sealed class TrayColorTableTests
{
    /// <summary>
    /// Verifies that the table's border color is the one <see cref="TrayColorTable.GetMenuBorder"/>
    /// returns, because the border DWM draws around the popup must match the menu's own palette.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MenuBorder_Palette_MatchesGetMenuBorder(bool isDark)
    {
        // Act
        var table = new TrayColorTable(isDark);

        // Assert
        Assert.Equal(TrayColorTable.GetMenuBorder(isDark), table.MenuBorder);
    }

    /// <summary>
    /// Verifies that the dark and the light palette use different border colors.
    /// </summary>
    [Fact]
    public void GetMenuBorder_DarkAndLight_ReturnsDifferentColors()
    {
        // Act
        var dark = TrayColorTable.GetMenuBorder(isDark: true);
        var light = TrayColorTable.GetMenuBorder(isDark: false);

        // Assert
        Assert.NotEqual(dark, light);
    }
}
