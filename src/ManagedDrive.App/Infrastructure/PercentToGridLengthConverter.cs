using System.Globalization;
using System.Windows.Data;

namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Converts a percentage (0-100) into a star <see cref="GridLength"/>, so a two-column grid can render a progress bar. The converter parameter <c>Fill</c> yields the filled share; any other value yields the remainder.
/// </summary>
public sealed class PercentToGridLengthConverter : IValueConverter
{
    /// <summary>
    /// Converts a percentage into a star <see cref="GridLength"/>.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value is double d ? Math.Clamp(d, 0, 100) : 0;
        var isFill = string.Equals(parameter as string, "Fill", StringComparison.OrdinalIgnoreCase);
        var star = isFill ? percent : 100 - percent;
        return new GridLength(Math.Max(star, 0.0001), GridUnitType.Star);
    }

    /// <summary>
    /// Not supported; the conversion is one-way.
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
