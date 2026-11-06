using System.Xml.Linq;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests the colour palettes as files: every brush the card, the tray popup and the sidebar refer to
/// must exist in each theme and style combination, because a missing key only shows up at run time
/// once that combination is selected.
/// </summary>
public sealed class PaletteKeysTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] SharedKeys =
        new[]
        {
            "AppWarning", "AppWarningSoft", "AppSuccess", "AppSuccessSoft", "AppDanger", "AppDangerSoft", "AppInfo", "AppInfoSoft",
            "AppPrimary", "AppPrimarySoft",
        }
        .Concat(Enumerable.Range(1, 6).SelectMany(i => new[] { $"AppAccent{i}", $"AppAccentSoft{i}" }))
        .ToArray();

    private static readonly string[] SidebarIconKeys =
        { "ToolIconNew", "ToolIconImport", "ToolIconRestore", "ToolIconSettings", "ToolIconAbout", "ToolIconExit" };

    /// <summary>
    /// Verifies that the keys the cards use exist in the theme palette alone and in the theme palette
    /// with a style palette merged over it, for every style and theme.
    /// </summary>
    [Theory]
    [InlineData("Light", null)]
    [InlineData("Dark", null)]
    [InlineData("Light", "B")]
    [InlineData("Dark", "B")]
    [InlineData("Light", "C")]
    [InlineData("Dark", "C")]
    public void Palette_DefinesEveryCardKey(string theme, string? style)
    {
        // Arrange
        var keys = KeysOf($"AppTheme.Colors.{theme}.xaml");
        if (style != null)
        {
            keys.UnionWith(KeysOf($"Styles/AppStyle.{style}.Colors.{theme}.xaml"));
        }

        // Act
        var missing = SharedKeys.Where(k => !keys.Contains(k)).ToList();

        // Assert
        Assert.Empty(missing);
    }

    /// <summary>
    /// Verifies that style B defines a colour for each sidebar icon in both themes.
    /// </summary>
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void StyleB_DefinesEverySidebarIconKey(string theme)
    {
        // Arrange
        var keys = KeysOf($"Styles/AppStyle.B.Colors.{theme}.xaml");

        // Act
        var missing = SidebarIconKeys.Where(k => !keys.Contains(k)).ToList();

        // Assert
        Assert.Empty(missing);
    }

    /// <summary>
    /// Verifies that a light palette and its dark counterpart define the same keys, so a theme switch
    /// never drops a brush.
    /// </summary>
    [Theory]
    [InlineData("AppTheme.Colors.{0}.xaml")]
    [InlineData("Styles/AppStyle.B.Colors.{0}.xaml")]
    [InlineData("Styles/AppStyle.C.Colors.{0}.xaml")]
    public void LightAndDarkPalettes_DefineTheSameKeys(string fileFormat)
    {
        // Arrange
        var light = KeysOf(string.Format(fileFormat, "Light"));
        var dark = KeysOf(string.Format(fileFormat, "Dark"));

        // Act
        var onlyLight = light.Except(dark).ToList();
        var onlyDark = dark.Except(light).ToList();

        // Assert
        Assert.Empty(onlyLight);
        Assert.Empty(onlyDark);
    }

    private static HashSet<string> KeysOf(string relativePath)
    {
        var path = Path.Combine(FindThemesDirectory(), relativePath);
        return XDocument.Load(path).Descendants()
            .Select(e => (string?)e.Attribute(X + "Key"))
            .OfType<string>()
            .ToHashSet();
    }

    private static string FindThemesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var themes = Path.Combine(dir.FullName, "src", "ManagedDrive.App", "Themes");
            if (Directory.Exists(themes))
            {
                return themes;
            }
        }

        throw new DirectoryNotFoundException("src/ManagedDrive.App/Themes was not found above the test output.");
    }
}
