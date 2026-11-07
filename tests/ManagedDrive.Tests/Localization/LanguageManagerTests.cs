using System.Text.RegularExpressions;
using ManagedDrive.App.Localization;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="LanguageManager"/>'s system-language resolution and that every language file
/// defines the same keys with the same placeholders.
/// </summary>
public sealed partial class LanguageManagerTests
{
    /// <summary>
    /// Verifies that Traditional Chinese cultures resolve to zh-TW, other Chinese to zh-CN.
    /// </summary>
    [Theory]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-MO", "zh-TW")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("zh-Hant-HK", "zh-TW")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hans-HK", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    [InlineData("zh", "zh-CN")]
    [InlineData("en-GB", "en-US")]
    [InlineData("en-US", "en-US")]
    [InlineData("ja-JP", "en-US")]
    [InlineData("", "en-US")]
    [InlineData(null, "en-US")]
    public void ResolveSystem_MapsCultureToSupportedTag(string? culture, string expected)
    {
        Assert.Equal(expected, LanguageManager.ResolveSystem(culture));
    }

    /// <summary>
    /// Verifies that a saved choice is returned unchanged.
    /// </summary>
    [Fact]
    public void Resolve_ReturnsSavedChoice()
    {
        Assert.Equal("zh-TW", LanguageManager.Resolve("zh-TW"));
    }

    /// <summary>
    /// Verifies that Traditional Chinese is selectable.
    /// </summary>
    [Fact]
    public void SupportedLanguages_IncludesTraditionalChinese()
    {
        Assert.Contains(LanguageManager.SupportedLanguages, l => l.Tag == "zh-TW");
    }

    /// <summary>
    /// Verifies that every language file has exactly the keys of the English one, and the same
    /// numbered placeholders in each string.
    /// </summary>
    [Theory]
    [InlineData("Strings.zh-CN.xaml")]
    [InlineData("Strings.zh-TW.xaml")]
    public void LanguageFile_HasSameKeysAndPlaceholdersAsEnglish(string file)
    {
        var english = Load("Strings.en-US.xaml");
        var other = Load(file);

        Assert.Equal(english.Keys.Order(), other.Keys.Order());
        foreach (var (key, value) in english)
        {
            Assert.True(
                Placeholders(value).SequenceEqual(Placeholders(other[key])),
                $"{file}: placeholders of '{key}' differ from English");
        }
    }

    private static Dictionary<string, string> Load(string file)
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", file));
        var result = new Dictionary<string, string>();
        foreach (Match m in StringEntry().Matches(xaml))
        {
            result[m.Groups["key"].Value] = m.Groups["value"].Value;
        }

        return result;
    }

    private static string[] Placeholders(string value) =>
        [.. PlaceholderRegex().Matches(value).Select(m => m.Groups[1].Value).Distinct().Order()];

    [GeneratedRegex("<sys:String x:Key=\"(?<key>[^\"]+)\">(?<value>[^<]*)</sys:String>")]
    private static partial Regex StringEntry();

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderRegex();
}
