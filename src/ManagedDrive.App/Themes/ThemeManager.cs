namespace ManagedDrive.App.Themes;

/// <summary>
/// Manages the color palette applied to the application: "light", "dark", or system default.
/// </summary>
public sealed class ThemeManager
{
    public static readonly ThemeManager Instance = new();

    private ResourceDictionary? _currentDict;
    private bool _subscribedToSystemEvents;

    private ThemeManager()
    {
    }

    /// <summary>
    /// Raised whenever the active color palette changes.
    /// </summary>
    public event EventHandler? ThemeChanged;

    /// <summary>
    /// Gets the resolved theme currently active in the UI. Always "light" or "dark".
    /// </summary>
    public string CurrentTheme { get; private set; } = "light";

    /// <summary>
    /// Gets the raw saved theme choice: "light", "dark", or <c>null</c> for system default.
    /// </summary>
    public string? SavedTheme
    {
        get; private set;
    }

    /// <summary>
    /// Resolves a saved theme choice to a concrete "light"/"dark" value.
    /// <c>null</c> or empty maps to the current Windows app theme.
    /// </summary>
    public static string Resolve(string? saved)
    {
        if (!string.IsNullOrEmpty(saved))
        {
            return saved;
        }

        return ReadSystemTheme();
    }

    /// <summary>
    /// Applies the theme given a saved choice (<c>null</c> = system default).
    /// Stores <paramref name="saved"/> in <see cref="SavedTheme"/>.
    /// </summary>
    public void Apply(string? saved)
    {
        SavedTheme = string.IsNullOrEmpty(saved) ? null : saved;
        SubscribeToSystemEvents();

        var tag = Resolve(saved);
        if (tag == CurrentTheme && _currentDict != null)
        {
            return;
        }

        ApplyConcrete(tag);
    }

    /// <summary>
    /// Applies the theme from a saved choice, used on startup.
    /// </summary>
    public void ApplyDefault(string? saved = null) => Apply(saved);

    private static string ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int intValue)
            {
                return intValue == 0 ? "dark" : "light";
            }
        }
        catch
        {
            // Fall through to the light default below.
        }

        return "light";
    }

    private void ApplyConcrete(string tag)
    {
        var dict = new ResourceDictionary
        {
            Source = new($"pack://application:,,,/Themes/AppTheme.Colors.{(tag == "dark" ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_currentDict != null)
        {
            merged.Remove(_currentDict);
        }

        merged.Add(dict);
        _currentDict = dict;
        CurrentTheme = tag;

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SubscribeToSystemEvents()
    {
        if (_subscribedToSystemEvents)
        {
            return;
        }

        _subscribedToSystemEvents = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General || SavedTheme != null)
            {
                return;
            }

            ApplyConcrete(ReadSystemTheme());
        };
    }
}