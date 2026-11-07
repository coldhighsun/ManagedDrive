using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Tells whether an item is the first one of its <see cref="ItemsControl"/>. It takes the item, the control, and the
/// control's item count: the count is only there so that the binding is evaluated again whenever items are added or
/// removed, which <see cref="ItemsControl.AlternationIndexProperty"/> does not do (it is set once, when a container is
/// prepared), so a row that becomes first after an insert or a removal would otherwise keep its old answer.
/// </summary>
public sealed class IsFirstItemConverter : IMultiValueConverter
{
    /// <summary>
    /// Converts the item, its items control and the item count into whether the item is at index 0.
    /// </summary>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length >= 2 && values[1] is ItemsControl { Items.Count: > 0 } owner && values[0] is { } item && Equals(owner.Items[0], item);

    /// <summary>
    /// Not supported; the conversion is one-way.
    /// </summary>
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
