namespace ManagedDrive.App.Helpers;

/// <summary>
/// Provides a <c>Hint.Text</c> attached property for watermark/placeholder text.
/// </summary>
public static class HintHelper
{
    /// <summary>
    /// Identifies the <c>Hint.Text</c> attached property.
    /// </summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(HintHelper),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary>
    /// Gets the hint text attached to <paramref name="obj"/>.
    /// </summary>
    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);

    /// <summary>
    /// Sets the hint text attached to <paramref name="obj"/>.
    /// </summary>
    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);
}
