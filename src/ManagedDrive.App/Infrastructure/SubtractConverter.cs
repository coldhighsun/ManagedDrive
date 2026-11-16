using System.Globalization;
using System.Windows.Data;

namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Subtracts the converter parameter from a number, never going below zero. Used to size a drop-down list from its
/// combo box: the popup adds a border and a scroll bar around the list, which must come off the combo box's width for
/// the two to be equally wide.
/// </summary>
public sealed class SubtractConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double number || double.IsNaN(number))
        {
            return Binding.DoNothing;
        }

        var amount = parameter is not null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
        return Math.Max(0, number - amount);
    }

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
