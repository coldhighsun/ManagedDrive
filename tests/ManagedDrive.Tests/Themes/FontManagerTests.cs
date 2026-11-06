using System.Text.RegularExpressions;
using ManagedDrive.App.Themes;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="FontManager"/>'s handling of a saved font choice and of the font list.
/// </summary>
public sealed partial class FontManagerTests
{
    private static readonly IReadOnlyList<FontChoice> Installed =
        [new("DengXian", "等线"), new("FZShuTi", "方正舒体")];

    /// <summary>
    /// Verifies that the Chinese list names a font with Simplified Chinese glyphs: without one WPF falls
    /// back per character and mixes fonts within a line.
    /// </summary>
    [Fact]
    public void ChineseFontFamily_NamesAChineseFont()
    {
        Assert.Contains("Microsoft YaHei UI", FontManager.ChineseFontFamily);
    }

    /// <summary>
    /// Verifies that a Chinese UI, Windows or region gets a Chinese list, the first one found deciding
    /// which, and anything else keeps WPF's fallback so that for example Japanese text keeps its glyph forms.
    /// </summary>
    [Theory]
    [InlineData("zh-CN", "en-US", "en-US", FontManager.ChineseFontFamily)]
    [InlineData("en-US", "zh-CN", "en-US", FontManager.ChineseFontFamily)]
    [InlineData("en-US", "en-US", "zh-CN", FontManager.ChineseFontFamily)]
    [InlineData("en-US", "zh-SG", "en-US", FontManager.ChineseFontFamily)]
    [InlineData("en-US", "zh", "en-US", FontManager.ChineseFontFamily)]
    [InlineData("en-US", "zh-Hans-HK", "en-US", FontManager.ChineseFontFamily)]
    [InlineData("en-US", "zh-TW", "en-US", FontManager.TraditionalChineseFontFamily)]
    [InlineData("en-US", "en-US", "zh-HK", FontManager.TraditionalChineseFontFamily)]
    [InlineData("en-US", "zh-MO", "en-US", FontManager.TraditionalChineseFontFamily)]
    [InlineData("en-US", "zh-Hant", "en-US", FontManager.TraditionalChineseFontFamily)]
    [InlineData("zh-CN", "zh-TW", "zh-TW", FontManager.ChineseFontFamily)]
    [InlineData("en-US", "en-US", "en-US", FontManager.DefaultFontFamily)]
    [InlineData("en-US", "ja-JP", "ja-JP", FontManager.DefaultFontFamily)]
    [InlineData("en-US", "zu-ZA", "", FontManager.DefaultFontFamily)]
    public void GetDefaultList_FollowsFirstChineseLanguage(string uiLanguage, string systemLanguage, string region, string expected)
    {
        Assert.Equal(expected, FontManager.GetDefaultList(uiLanguage, systemLanguage, region));
    }

    /// <summary>
    /// Verifies that an empty choice is stored as <c>null</c> and a font name without surrounding spaces.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("DengXian", "DengXian")]
    [InlineData("  DengXian ", "DengXian")]
    public void Normalize_SavedChoice_IsNullWhenEmpty(string? saved, string? expected)
    {
        Assert.Equal(expected, FontManager.Normalize(saved));
    }

    /// <summary>
    /// Verifies that no choice gives the default list.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compose_NoChoice_ReturnsDefaultList(string? saved)
    {
        Assert.Equal(FontManager.ChineseFontFamily, FontManager.Compose(saved, FontManager.ChineseFontFamily));
    }

    /// <summary>
    /// Verifies that a chosen font comes first and keeps the default list behind it, for the characters
    /// it lacks and for when it is uninstalled.
    /// </summary>
    [Fact]
    public void Compose_Choice_PutsItBeforeDefaultList()
    {
        Assert.Equal(
            $"DengXian, {FontManager.ChineseFontFamily}",
            FontManager.Compose(" DengXian ", FontManager.ChineseFontFamily));
    }

    /// <summary>
    /// Verifies that font names compare without regard to case, and that no choice matches no font.
    /// </summary>
    [Theory]
    [InlineData("FZShuTi", true)]
    [InlineData(" fzshuti ", true)]
    [InlineData("DengXian", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSameFont_ComparesNamesIgnoringCase(string? choice, bool expected)
    {
        Assert.Equal(expected, FontManager.IsSameFont(choice, "FZShuTi"));
    }

    /// <summary>
    /// Verifies that an installed or absent choice adds nothing, so it can't be listed twice and an installed
    /// font keeps its sorted place, also when the saved name differs in case.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("FZShuTi")]
    [InlineData("fzshuti")]
    public void GetChoices_InstalledOrNoChoice_ReturnsInstalledFonts(string? choice)
    {
        Assert.Equal(Installed, FontManager.GetChoices(Installed, choice, name => $"{name}?"));
    }

    /// <summary>
    /// Verifies that a font that is gone stays in the list, first and marked as missing.
    /// </summary>
    [Fact]
    public void GetChoices_MissingChoice_ListsItFirstUnderMissingName()
    {
        var choices = FontManager.GetChoices(Installed, "Gone", name => $"{name}?");

        Assert.Equal([new("Gone", "Gone?"), .. Installed], choices);
    }

    /// <summary>
    /// Verifies the real font list: text fonts by display name, without symbol fonts.
    /// </summary>
    [Fact]
    public void ListInstalledFonts_ListsSortedTextFontsOnly()
    {
        var fonts = FontManager.ListInstalledFonts("en-US");

        Assert.Contains(fonts, font => font.Source == "Segoe UI");
        Assert.DoesNotContain(fonts, font => font.Source == "Wingdings");
        Assert.Equal(fonts.OrderBy(font => font.DisplayName, StringComparer.CurrentCultureIgnoreCase), fonts);
    }

    /// <summary>
    /// Verifies that the app names no text font itself, in XAML or in code: that brings back the mixed-font
    /// text <see cref="FontManager"/> exists to prevent. Text has to read the <c>AppFontFamily</c> resource;
    /// only the icon font may be named.
    /// </summary>
    [Fact]
    public void App_DoesNotHardCodeATextFont()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ManagedDrive.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var app = Path.Combine(root.FullName, "src", "ManagedDrive.App");
        var xaml = Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories);
        var code = Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories);

        Assert.NotEmpty(xaml);
        Assert.NotEmpty(code);
        var named = xaml
            .SelectMany(file => FontAssignment().Matches(File.ReadAllText(file)).Select(match => (file, font: match.Groups["font"].Value)))
            .Concat(code.SelectMany(file => FontLiteral().Matches(File.ReadAllText(file)).Select(match => (file, font: match.Groups["font"].Value))))
            .Where(found => found.font != "Segoe Fluent Icons" && !found.font.StartsWith('{'))
            .ToList();

        Assert.Empty(named);
    }

    /// <summary>
    /// Verifies that both language files mark a missing font with the placeholder for its name.
    /// </summary>
    [Theory]
    [InlineData("Strings.en-US.xaml")]
    [InlineData("Strings.zh-CN.xaml")]
    public void MissingFontString_ExistsInEveryLanguageWithItsPlaceholder(string file)
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", file));

        var match = Regex.Match(xaml, "x:Key=\"Settings.Font.Missing\">([^<]*)<");

        Assert.True(match.Success);
        Assert.Contains("{0}", match.Groups[1].Value);
    }

    [GeneratedRegex("(?:\\bFontFamily=|Property=\"FontFamily\"\\s+Value=)\"(?<font>[^\"]*)\"")]
    private static partial Regex FontAssignment();

    [GeneratedRegex("new\\s+(?:FontFamily|Typeface)\\(\\s*\"(?<font>[^\"]*)\"")]
    private static partial Regex FontLiteral();
}
