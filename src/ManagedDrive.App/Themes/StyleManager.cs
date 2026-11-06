namespace ManagedDrive.App.Themes;

/// <summary>
/// Manages the visual style (shape, layout and card templates) of the application: "b" (dashboard) or
/// "c" (minimal). It is independent of <see cref="ThemeManager"/>, which only swaps the light/dark palette.
/// Style "a" (Fluent) is no longer a choice: <c>Themes/Styles/AppStyle.A.xaml</c> stays as the base
/// dictionary that B and C merge and override.
/// </summary>
public sealed class StyleManager
{
    /// <summary>
    /// The style used when nothing was saved or the saved value is unknown.
    /// </summary>
    public const string DefaultStyle = "b";

    /// <summary>
    /// The single shared instance.
    /// </summary>
    public static readonly StyleManager Instance = new();

    private ResourceDictionary? _currentDict;
    private ResourceDictionary? _paletteDict;

    private StyleManager()
    {
        // A style's own palette is merged after the theme's colors so it wins, so it has to be
        // swapped again whenever the light/dark palette is.
        ThemeManager.Instance.ThemeChanged += (_, _) => ApplyPalette();
    }

    /// <summary>
    /// Gets the style tags the application ships, in the order the settings dialog lists them.
    /// </summary>
    public static IReadOnlyList<string> SupportedStyles { get; } = ["b", "c"];

    /// <summary>
    /// Gets the style tags that bring their own light and dark palette
    /// (<c>Themes/Styles/AppStyle.{X}.Colors.{Light|Dark}.xaml</c>) on top of the theme's colors.
    /// </summary>
    private static IReadOnlySet<string> StylesWithPalette { get; } = new HashSet<string> { "b", "c" };

    /// <summary>
    /// Raised whenever the active style changes.
    /// </summary>
    public event EventHandler? StyleChanged;

    /// <summary>
    /// Gets the style currently active in the UI. Always one of <see cref="SupportedStyles"/>.
    /// </summary>
    public string CurrentStyle { get; private set; } = DefaultStyle;

    /// <summary>
    /// Gets the saved style choice (see <see cref="Normalize"/>), or <c>null</c> for the default.
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
    /// Gets the choice to persist for a saved or picked one: <c>null</c> when it means the default style
    /// (including an empty, retired or unknown value, which resolve to it), otherwise the style's tag.
    /// A value that is not kept as it is can not turn into a different style later, if a style is ever
    /// added under that name.
    /// </summary>
    /// <param name="saved">The saved or picked choice.</param>
    /// <returns>The choice to store, or <c>null</c> for the default.</returns>
    public static string? Normalize(string? saved)
    {
        var tag = Resolve(saved);
        return tag == DefaultStyle ? null : tag;
    }

    /// <summary>
    /// Returns whether the style <paramref name="tag"/> ships its own palette on top of the theme's colors.
    /// </summary>
    public static bool HasPalette(string tag) => StylesWithPalette.Contains(tag);

    /// <summary>
    /// Applies the style given a saved choice (<c>null</c> = default) and stores it in <see cref="SavedStyle"/>.
    /// </summary>
    public void Apply(string? saved)
    {
        SavedStyle = Normalize(saved);

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
        ApplyPalette();

        StyleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces the style's palette dictionary with the one for the current style and theme, if the style has one.
    /// </summary>
    private void ApplyPalette()
    {
        if (Application.Current is null)
        {
            return;
        }

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_paletteDict != null)
        {
            merged.Remove(_paletteDict);
            _paletteDict = null;
        }

        if (!HasPalette(CurrentStyle))
        {
            return;
        }

        var mode = ThemeManager.Instance.CurrentTheme == "dark" ? "Dark" : "Light";
        _paletteDict = new()
        {
            Source = new($"pack://application:,,,/Themes/Styles/AppStyle.{CurrentStyle.ToUpperInvariant()}.Colors.{mode}.xaml", UriKind.Absolute),
        };
        merged.Add(_paletteDict);
    }
}
