namespace ManagedDrive.App.Themes;

/// <summary>
/// Manages the visual style (shape, layout and card templates) of the application: "a" (Fluent),
/// "b" (dashboard) or "c" (minimal). It is independent of <see cref="ThemeManager"/>, which only
/// swaps the light/dark palette.
/// </summary>
public sealed class StyleManager
{
    /// <summary>
    /// The style used when nothing was saved or the saved value is unknown.
    /// </summary>
    public const string DefaultStyle = "a";

    /// <summary>
    /// The single shared instance.
    /// </summary>
    public static readonly StyleManager Instance = new();

    private ResourceDictionary? _currentDict;

    private StyleManager()
    {
    }

    /// <summary>
    /// Gets the style tags the application ships, in the order the settings dialog lists them.
    /// </summary>
    public static IReadOnlyList<string> SupportedStyles { get; } = ["a", "b", "c"];

    /// <summary>
    /// Raised whenever the active style changes.
    /// </summary>
    public event EventHandler? StyleChanged;

    /// <summary>
    /// Gets the style currently active in the UI. Always one of <see cref="SupportedStyles"/>.
    /// </summary>
    public string CurrentStyle { get; private set; } = DefaultStyle;

    /// <summary>
    /// Gets the raw saved style choice, or <c>null</c> for the default.
    /// </summary>
    public string? SavedStyle
    {
        get; private set;
    }

    /// <summary>
    /// Maps a saved style choice to a supported tag; null, empty or unknown values give <see cref="DefaultStyle"/>.
    /// </summary>
    public static string Resolve(string? saved)
    {
        if (string.IsNullOrEmpty(saved))
        {
            return DefaultStyle;
        }

        var tag = saved.ToLowerInvariant();
        return SupportedStyles.Contains(tag) ? tag : DefaultStyle;
    }

    /// <summary>
    /// Applies the style given a saved choice (<c>null</c> = default) and stores it in <see cref="SavedStyle"/>.
    /// </summary>
    public void Apply(string? saved)
    {
        SavedStyle = string.IsNullOrEmpty(saved) ? null : saved;

        var tag = Resolve(saved);
        if (tag == CurrentStyle && _currentDict != null)
        {
            return;
        }

        ApplyConcrete(tag);
    }

    /// <summary>
    /// Applies the style from a saved choice, used on startup.
    /// </summary>
    public void ApplyDefault(string? saved = null) => Apply(saved);

    private void ApplyConcrete(string tag)
    {
        var dict = new ResourceDictionary
        {
            Source = new($"pack://application:,,,/Themes/Styles/AppStyle.{tag.ToUpperInvariant()}.xaml", UriKind.Absolute),
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_currentDict != null)
        {
            merged.Remove(_currentDict);
        }

        merged.Add(dict);
        _currentDict = dict;
        CurrentStyle = tag;

        StyleChanged?.Invoke(this, EventArgs.Empty);
    }
}
