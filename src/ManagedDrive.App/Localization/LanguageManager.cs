using System.Windows;

namespace ManagedDrive.App.Localization;

public sealed class LanguageManager
{
    public static readonly LanguageManager Instance = new();

    private ResourceDictionary? _currentDict;

    private LanguageManager() { }

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage { get; private set; } = "en-US";

    public static IReadOnlyList<(string Tag, string DisplayName)> SupportedLanguages { get; } =
    [
        ("en-US", "English"),
        ("zh-CN", "中文（简体）"),
    ];

    public void ApplyDefault(string languageTag = "en-US") => Apply(languageTag);

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
            merged.Remove(_currentDict);

        merged.Add(dict);
        _currentDict = dict;
        CurrentLanguage = languageTag;

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
