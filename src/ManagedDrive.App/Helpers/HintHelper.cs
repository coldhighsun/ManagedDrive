using System.Windows;

namespace ManagedDrive.App.Helpers;

/// <summary>
/// Provides a <c>Hint.Text</c> attached property for watermark/placeholder text.
/// </summary>
public static class HintHelper
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(HintHelper),
            new FrameworkPropertyMetadata(string.Empty));

    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);

    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);
}