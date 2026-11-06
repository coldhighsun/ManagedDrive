using System.Globalization;
using System.Windows.Markup;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;

namespace ManagedDrive.App.Themes;

/// <summary>
/// Manages the UI font: the <c>AppFontFamily</c> resource every window, context menu and tooltip reads.
/// Segoe UI has no Chinese glyphs: left to itself, WPF falls back per character by the element's language
/// (en-US here, which tries Yu Gothic UI first). That font lacks Simplified Chinese characters such as 拟
/// and 盘, so one line of text ends up in two fonts of different weight. Where Chinese text is expected,
/// the list therefore names a Chinese font; elsewhere WPF's fallback stays, so that for example Japanese
/// file names keep their Japanese glyph forms.
/// </summary>
public sealed class FontManager
{
    /// <summary>
    /// The default font list where no Chinese text is expected.
    /// </summary>
    public const string DefaultFontFamily = "Segoe UI";

    /// <summary>
    /// The default font list where Simplified Chinese text is expected.
    /// </summary>
    public const string ChineseFontFamily = "Segoe UI, Microsoft YaHei UI";

    /// <summary>
    /// The default font list where Traditional Chinese text is expected.
    /// </summary>
    public const string TraditionalChineseFontFamily = "Segoe UI, Microsoft JhengHei UI";

    /// <summary>
    /// The key of the application resource that holds the current <see cref="FontFamily"/>.
    /// </summary>
    private const string ResourceKey = "AppFontFamily";

    private static readonly ILogger Logger = AppLog.CreateLogger<FontManager>();

    /// <summary>
    /// The single shared instance.
    /// </summary>
    public static readonly FontManager Instance = new();

    private string? _appliedList;
    private (string LanguageTag, IReadOnlyList<FontChoice> Fonts)? _installedFonts;

    private FontManager()
    {
        // The default list depends on the UI language, so it has to be chosen again when that changes.
        LanguageManager.Instance.LanguageChanged += (_, _) => ApplyCurrent();
    }

    /// <summary>
    /// Gets the saved font choice (see <see cref="Normalize"/>), or <c>null</c> for the default.
    /// </summary>
    public string? SavedFont
    {
        get; private set;
    }

    /// <summary>
    /// Gets the choice to persist for a saved or picked one: the font name, or <c>null</c> for the default.
    /// </summary>
    /// <param name="saved">The saved or picked choice.</param>
    /// <returns>The choice to store, or <c>null</c> for the default.</returns>
    public static string? Normalize(string? saved) => string.IsNullOrWhiteSpace(saved) ? null : saved.Trim();

    /// <summary>
    /// Gets the default font list: the Chinese list for the first of <paramref name="languageTags"/> that is
    /// Chinese, otherwise <see cref="DefaultFontFamily"/>. Pass the UI language first: an English UI on a
    /// Chinese Windows or in a Chinese region still shows Chinese disk labels and file names.
    /// </summary>
    /// <param name="languageTags">The language tags to look at, the most relevant first.</param>
    /// <returns>The comma-separated font list.</returns>
    public static string GetDefaultList(params string[] languageTags) =>
        languageTags.Select(GetChineseList).FirstOrDefault(list => list != null) ?? DefaultFontFamily;

    /// <summary>
    /// Builds the font list for a saved choice. The default list stays behind a chosen font, so characters
    /// it lacks, or all of them once it is uninstalled, still show properly.
    /// </summary>
    /// <param name="saved">The saved choice (<c>null</c> = default).</param>
    /// <param name="defaultList">The default list, from <see cref="GetDefaultList"/>.</param>
    /// <returns>The comma-separated font list.</returns>
    public static string Compose(string? saved, string defaultList) =>
        Normalize(saved) is { } font ? $"{font}, {defaultList}" : defaultList;

    /// <summary>
    /// Returns whether <paramref name="choice"/> names the font <paramref name="source"/>. Font names are
    /// not case-sensitive.
    /// </summary>
    /// <param name="choice">The saved or picked choice.</param>
    /// <param name="source">The name of an installed font.</param>
    /// <returns><c>true</c> if they are the same font.</returns>
    public static bool IsSameFont(string? choice, string source) =>
        string.Equals(Normalize(choice), source, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the entries a font picker lists: the installed fonts, preceded by <paramref name="choice"/>
    /// under <paramref name="missingName"/> when it is not one of them. A font that is gone stays
    /// selectable, because it comes back into use when it is installed again.
    /// </summary>
    /// <param name="installed">The installed fonts.</param>
    /// <param name="choice">The saved or picked choice.</param>
    /// <param name="missingName">Gives the name to show for a font that is not installed.</param>
    /// <returns>The entries to list.</returns>
    public static IReadOnlyList<FontChoice> GetChoices(
        IReadOnlyList<FontChoice> installed, string? choice, Func<string, string> missingName)
    {
        if (Normalize(choice) is not { } font || installed.Any(entry => IsSameFont(font, entry.Source)))
        {
            return installed;
        }

        return [new(font, missingName(font)), .. installed];
    }

    /// <summary>
    /// Lists the installed text fonts sorted by display name, leaving out symbol fonts. Opening every font
    /// takes a noticeable time, so call this off the UI thread.
    /// </summary>
    /// <param name="languageTag">The language to prefer for the display names.</param>
    /// <returns>The installed fonts.</returns>
    public static IReadOnlyList<FontChoice> ListInstalledFonts(string languageTag)
    {
        var language = XmlLanguage.GetLanguage(languageTag);
        return Fonts.SystemFontFamilies
            .Where(IsTextFont)
            .Select(family => new FontChoice(family.Source, family.FamilyNames.TryGetValue(language, out var name) ? name : family.Source))
            .OrderBy(font => font.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Gets <see cref="ListInstalledFonts"/> off the UI thread, reading the fonts once per language. Reading
    /// them again would not help: WPF builds <see cref="Fonts.SystemFontFamilies"/> once per process, so a
    /// font installed while the app runs only shows up after a restart anyway. Call this on the UI thread.
    /// </summary>
    /// <param name="languageTag">The language to prefer for the display names.</param>
    /// <returns>The installed fonts.</returns>
    public async Task<IReadOnlyList<FontChoice>> GetInstalledFontsAsync(string languageTag)
    {
        if (_installedFonts is { } cached && cached.LanguageTag == languageTag)
        {
            return cached.Fonts;
        }

        var fonts = await Task.Run(() => ListInstalledFonts(languageTag));
        _installedFonts = (languageTag, fonts);
        return fonts;
    }

    /// <summary>
    /// Applies the font given a saved choice (<c>null</c> = default) and stores it in <see cref="SavedFont"/>.
    /// </summary>
    /// <param name="saved">The saved choice.</param>
    public void Apply(string? saved)
    {
        SavedFont = Normalize(saved);
        ApplyCurrent();
    }

    /// <summary>
    /// Applies the font from a saved choice, used on startup.
    /// </summary>
    /// <param name="saved">The saved choice.</param>
    public void ApplyDefault(string? saved = null) => Apply(saved);

    /// <summary>
    /// Gets the Chinese font list for a language tag, or <c>null</c> when it is not Chinese. An explicit
    /// script wins; without one, Taiwan, Hong Kong and Macao mean Traditional.
    /// </summary>
    private static string? GetChineseList(string languageTag)
    {
        var subtags = languageTag.Split('-');
        if (!subtags[0].Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        bool Has(params string[] values) => subtags.Any(tag => values.Contains(tag, StringComparer.OrdinalIgnoreCase));

        return Has("Hant") || (!Has("Hans") && Has("TW", "HK", "MO")) ? TraditionalChineseFontFamily : ChineseFontFamily;
    }

    /// <summary>
    /// Returns whether <paramref name="family"/> can be opened and is not a symbol font. Whatever goes wrong
    /// with one font file only drops that font from the list.
    /// </summary>
    private static bool IsTextFont(FontFamily family)
    {
        try
        {
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            return typeface.TryGetGlyphTypeface(out var glyphs) && !glyphs.Symbol;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not open font {Font}; leaving it out of the font list.", family.Source);
            return false;
        }
    }

    /// <summary>
    /// Puts the font list for <see cref="SavedFont"/> and the current language in the application resources.
    /// </summary>
    private void ApplyCurrent()
    {
        var defaultList = GetDefaultList(
            LanguageManager.Instance.CurrentLanguage, CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentCulture.Name);
        var list = Compose(SavedFont, defaultList);
        if (list == _appliedList)
        {
            return;
        }

        Application.Current.Resources[ResourceKey] = new FontFamily(list);
        _appliedList = list;
    }
}
