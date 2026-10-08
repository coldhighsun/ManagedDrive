using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Turns the theme resource key of a space-usage row (see <see cref="SpaceBrushKeys"/>) into the brush of the current theme.
/// It is used as a multi-value converter so the binding can list a theme-dependent resource as a second input: when the theme
/// changes that input changes and the colour is looked up again, without the list being rebuilt.
/// </summary>
public sealed class SpaceSwatchConverter : IMultiValueConverter
{
    /// <summary>
    /// Looks the brush up.
    /// </summary>
    /// <param name="values">The key (a <see cref="string"/>) first; further values only trigger a new lookup.</param>
    /// <param name="targetType">Unused.</param>
    /// <param name="parameter">Unused.</param>
    /// <param name="culture">Unused.</param>
    /// <returns>
    /// The brush of the key; the "other" colour when there is no key; grey when the theme lacks the brush (as in the map).
    /// </returns>
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current?.TryFindResource(KeyOf(values)) as Brush ?? Brushes.Gray;

    /// <summary>
    /// Picks the resource key to look up.
    /// </summary>
    /// <param name="values">The binding values; the key (a <see cref="string"/>) is the first.</param>
    /// <returns>The given key, or the "other" key when there is none.</returns>
    internal static string KeyOf(object?[] values) =>
        values.Length > 0 && values[0] is string { Length: > 0 } key ? key : SpaceBrushKeys.For(FileCategory.Other);

    /// <summary>
    /// Not supported; the conversion is one-way.
    /// </summary>
    /// <param name="value">Unused.</param>
    /// <param name="targetTypes">Unused.</param>
    /// <param name="parameter">Unused.</param>
    /// <param name="culture">Unused.</param>
    /// <returns>Never returns.</returns>
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
