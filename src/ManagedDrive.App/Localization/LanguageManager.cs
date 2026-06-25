using System.Windows;

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
    /// Gets or sets the current language for the application.
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
    /// Gets or sets the current language for the application.
    /// </summary>
    public string CurrentLanguage { get; private set; } = "en-US";

    /// <summary>
    /// Sets the current language for the application.
    /// </summary>
    /// <param name="languageTag">The language tag to apply.</param>
    public void Apply(string languageTag)
    {
        ResourceDictionary dict;
        try
        {
            dict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Localization/Strings.{languageTag}.xaml", UriKind.Absolute),
            };
        }
        catch
        {
            if (languageTag != "en-US")
            {
                Apply("en-US");
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
        CurrentLanguage = languageTag;

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyDefault(string languageTag = "en-US") => Apply(languageTag);
}