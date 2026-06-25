using System.Windows;

namespace ManagedDrive.App.Localization;

/// <summary>
/// Provides access to localized strings for the application.
/// </summary>
public static class Loc
{
    /// <summary>
    /// Gets the localized string for the specified key.
    /// </summary>
    /// <param name="key">The key of the localized string.</param>
    /// <param name="args">Optional arguments to format the string.</param>
    /// <returns>The localized string.</returns>
    public static string Format(string key, params object?[] args) =>
        string.Format(Get(key), args);

    /// <summary>
    /// Gets the localized string for the specified key.
    /// </summary>
    /// <param name="key">The key of the localized string.</param>
    /// <returns>The localized string.</returns>
    public static string Get(string key) =>
            Application.Current?.Resources[key] is string value ? value : key;
}