using System.Globalization;
using System.Windows.Data;

namespace ManagedDrive.App.Infrastructure;

public sealed class PercentToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value is double d ? Math.Clamp(d, 0, 100) : 0;
        var isFill = string.Equals(parameter as string, "Fill", StringComparison.OrdinalIgnoreCase);
        var star = isFill ? percent : 100 - percent;
        return new GridLength(Math.Max(star, 0.0001), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}