using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

namespace ManagedDrive.App.Controls;

/// <summary>
/// A miniature drawing of the main window in a theme or a visual style, for the choice cards of the settings dialog.
/// In <c>theme</c> mode <see cref="Value"/> is <c>light</c>, <c>dark</c> or empty (follow the system, drawn half and half) and
/// the current style is used; in <c>style</c> mode <see cref="Value"/> is the style tag and the current theme is used.
/// The colours come from <see cref="StylePreviewPalette"/>, so nothing has to be applied to show them.
/// </summary>
public sealed class StylePreview : FrameworkElement
{
    /// <summary>
    /// Identifies the <see cref="Mode"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(string), typeof(StylePreview),
        new FrameworkPropertyMetadata("theme", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Identifies the <see cref="Value"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(StylePreview),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Gets or sets what the preview shows: <c>theme</c> (light, dark or system) or <c>style</c> (dashboard or minimal).
    /// </summary>
    public string Mode
    {
        get => (string)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>
    /// Gets or sets the theme (<c>light</c>, <c>dark</c>, empty for the system) or the style tag to draw.
    /// </summary>
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 8 || bounds.Height < 8)
        {
            return;
        }

        var currentStyle = StyleManager.Instance.CurrentStyle;
        var currentDark = ThemeManager.Instance.CurrentTheme == "dark";

        if (string.Equals(Mode, "style", StringComparison.OrdinalIgnoreCase))
        {
            DrawWindow(dc, bounds, StylePreviewPalette.IsMinimal(Value), StylePreviewPalette.For(Value, currentDark));
            return;
        }

        var minimal = StylePreviewPalette.IsMinimal(currentStyle);
        if (string.IsNullOrEmpty(Value))
        {
            // Follow the system: the same window, light on the left half and dark on the right half.
            var half = bounds.Width / 2;
            dc.PushClip(new RectangleGeometry(new(0, 0, half, bounds.Height)));
            DrawWindow(dc, bounds, minimal, StylePreviewPalette.For(currentStyle, dark: false));
            dc.Pop();
            dc.PushClip(new RectangleGeometry(new(half, 0, bounds.Width - half, bounds.Height)));
            DrawWindow(dc, bounds, minimal, StylePreviewPalette.For(currentStyle, dark: true));
            dc.Pop();
            return;
        }

        DrawWindow(dc, bounds, minimal, StylePreviewPalette.For(currentStyle, string.Equals(Value, "dark", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Draws a small main window: a sidebar of coloured icons and raised cards for the dashboard style, a flat list with
    /// hairlines for the minimal one.
    /// </summary>
    private static void DrawWindow(DrawingContext dc, Rect bounds, bool minimal, StylePreviewColors colors)
    {
        dc.DrawRectangle(Fill(colors.Background), null, bounds);

        var listLeft = bounds.Left;
        if (minimal)
        {
            // Icons in a row at the top right, as the minimal style puts its tool buttons in the app bar.
            for (var i = 0; i < 4; i++)
            {
                dc.DrawEllipse(Fill(colors.Accents[i]), null, new(bounds.Right - 8 - i * 7, bounds.Top + 7), 2, 2);
            }

            listLeft = bounds.Left + 6;
        }
        else
        {
            var sidebar = new Rect(bounds.Left, bounds.Top, bounds.Width * 0.14, bounds.Height);
            dc.DrawRectangle(Fill(colors.Sidebar), null, sidebar);
            dc.DrawRectangle(Fill(colors.Divider), null, new(sidebar.Right - 0.5, sidebar.Top, 0.5, sidebar.Height));
            for (var i = 0; i < 4; i++)
            {
                dc.DrawEllipse(Fill(colors.Accents[i]), null, new(sidebar.Left + sidebar.Width / 2, bounds.Top + 10 + i * 9), 2.2, 2.2);
            }

            listLeft = sidebar.Right + 5;
        }

        var top = bounds.Top + (minimal ? 14 : 6);
        var width = bounds.Right - 5 - listLeft;
        var rowHeight = minimal ? 17 : 19;
        var gap = minimal ? 0 : 4;
        for (var row = 0; row < 2; row++)
        {
            var rect = new Rect(listLeft, top + row * (rowHeight + gap), width, rowHeight);
            DrawRow(dc, rect, minimal, colors, row);
            if (minimal && row > 0)
            {
                dc.DrawRectangle(Fill(colors.Divider), null, new(rect.Left + 3, rect.Top, rect.Width - 6, 0.6));
            }
        }
    }

    /// <summary>
    /// Draws one disk row: a drive-letter badge, a name bar and a usage bar.
    /// </summary>
    private static void DrawRow(DrawingContext dc, Rect rect, bool minimal, StylePreviewColors colors, int index)
    {
        if (!minimal)
        {
            dc.DrawRoundedRectangle(Fill(colors.Surface), new Pen(Fill(colors.Divider), 0.6), rect, 3, 3);
        }

        var badge = new Rect(rect.Left + 4, rect.Top + (rect.Height - 8) / 2, 8, 8);
        var accent = colors.Accents[index % colors.Accents.Count];
        dc.DrawRoundedRectangle(minimal ? null : Fill(accent, 0.25), minimal ? new Pen(Fill(accent), 0.8) : null, badge, minimal ? 0 : 2, minimal ? 0 : 2);

        var barLeft = badge.Right + 4;
        var barWidth = rect.Right - 5 - barLeft;
        dc.DrawRectangle(Fill(colors.Text, 0.75), null, new(barLeft, rect.Top + 4, barWidth * 0.4, 2));

        var track = new Rect(barLeft, rect.Bottom - 6, barWidth, 2);
        dc.DrawRoundedRectangle(Fill(colors.Track), null, track, 1, 1);
        dc.DrawRoundedRectangle(Fill(colors.Fill), null, new(track.Left, track.Top, track.Width * (index == 0 ? 0.35 : 0.7), track.Height), 1, 1);
    }

    private static Brush Fill(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}
