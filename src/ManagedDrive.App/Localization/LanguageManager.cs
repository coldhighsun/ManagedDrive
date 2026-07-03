using System.Globalization;

namespace ManagedDrive.App.Localization;

/// <summary>
/// Provides access to localized strings for the application.
/// </summary>
public sealed class LanguageManager
{
    public static readonly LanguageManager Instance = new();

    private ResourceDictionary? _currentDict;

    private LanguageManager()
    {
    }

    /// <summary>
    /// Sets the current language for the application.
    /// </summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// Gets the list of explicitly selectable languages.
    /// </summary>
    public static IReadOnlyList<(string Tag, string DisplayName)> SupportedLanguages
    {
        get;
    } =
    [
        ("en-US", "English"),
        ("zh-CN", "中文（简体）"),
    ];

    /// <summary>
    /// Gets the resolved language tag currently active in the UI.
    /// Always a concrete tag like "en-US" or "zh-CN".
    /// </summary>
    public string CurrentLanguage { get; private set; } = "en-US";

    /// <summary>
    /// Gets the raw saved language choice: a concrete tag, or <c>null</c> for system default.
    /// </summary>
    public string? SavedLanguage
    {
        get; private set;
    }

    /// <summary>
    /// Resolves a saved language choice to a concrete tag.
    /// <c>null</c> or empty maps to the best-matching supported language for the
    /// current system locale, falling back to "en-US".
    /// </summary>
    public static string Resolve(string? saved)
    {
        if (!string.IsNullOrEmpty(saved))
        {
            return saved;
        }

        var iso = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        foreach (var (tag, _) in SupportedLanguages)
        {
            if (tag.StartsWith(iso, StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        return "en-US";
    }

    /// <summary>
    /// Applies the language given a saved choice (<c>null</c> = system default).
    /// Stores <paramref name="saved"/> in <see cref="SavedLanguage"/>.
    /// </summary>
    public void Apply(string? saved)
    {
        SavedLanguage = string.IsNullOrEmpty(saved) ? null : saved;
        var tag = Resolve(saved);
        if (tag == CurrentLanguage)
        {
            return;
        }

        ApplyConcrete(tag);
    }

    /// <summary>
    /// Applies the language from a saved choice, used on startup.
    /// </summary>
    public void ApplyDefault(string? saved = null) => Apply(saved);

    private void ApplyConcrete(string tag)
    {
        ResourceDictionary dict;
        try
        {
            dict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Localization/Strings.{tag}.xaml", UriKind.Absolute),
            };
        }
        catch
        {
            if (tag != "en-US")
            {
                ApplyConcrete("en-US");
                return;
            }
            throw;
        }

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_currentDict != null)
        {
            merged.Remove(_currentDict);
        }

        merged.Add(dict);
        _currentDict = dict;
        CurrentLanguage = tag;

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}