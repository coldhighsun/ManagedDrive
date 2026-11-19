using System.Drawing.Drawing2D;

namespace ManagedDrive.App.Services;

/// <summary>
/// Where the parts of a tray icon of one size go: the read light bottom left, the write light
/// bottom right, the alert badge top right. Pure geometry, so the rules are testable.
/// </summary>
/// <param name="Size">The edge length of the icon.</param>
/// <param name="ReadLight">The bounds of the read light.</param>
/// <param name="WriteLight">The bounds of the write light.</param>
/// <param name="Badge">The bounds of the alert badge.</param>
/// <param name="Outline">The width of the dark outline that keeps lights and badge visible on any taskbar colour.</param>
public readonly record struct TrayIconLayout(int Size, Rectangle ReadLight, Rectangle WriteLight, Rectangle Badge, float Outline)
{
    /// <summary>
    /// Lays out an icon of one size. A light is about a third of the icon (5 px at 16 px), the
    /// badge a little over half (9 px at 16 px), so both still read at 100% scaling.
    /// </summary>
    /// <param name="size">The edge length of the icon, in pixels.</param>
    /// <returns>The layout.</returns>
    public static TrayIconLayout For(int size)
    {
        var light = Math.Max(5, (int)Math.Round(size * 0.34));
        var badge = Math.Max(9, (int)Math.Round(size * 0.56));
        return new(
            size,
            new(0, size - light, light, light),
            new(size - light, size - light, light, light),
            new(size - badge, 0, badge, badge),
            Math.Max(1f, size / 16f));
    }
}

/// <summary>
/// Draws the tray icon for each <see cref="TrayIconVisual"/> at one size and keeps the results:
/// there are at most twelve (two lights times three badges). Owns the HICONs it creates, which
/// <see cref="Icon.Dispose"/> alone would leak, and releases them in <see cref="Dispose"/>, so the
/// caller must stop showing them first.
/// </summary>
public sealed class TrayIconRenderer : IDisposable
{
    /// <summary>
    /// The read light colour.
    /// </summary>
    private static readonly Color ReadColor = Color.FromArgb(255, 0, 214, 110);

    /// <summary>
    /// The write light colour.
    /// </summary>
    private static readonly Color WriteColor = Color.FromArgb(255, 255, 120, 0);

    /// <summary>
    /// The warning badge colour.
    /// </summary>
    private static readonly Color WarningColor = Color.FromArgb(255, 255, 185, 0);

    /// <summary>
    /// The critical badge colour.
    /// </summary>
    private static readonly Color CriticalColor = Color.FromArgb(255, 232, 17, 35);

    /// <summary>
    /// The outline colour around lights and badges.
    /// </summary>
    private static readonly Color OutlineColor = Color.FromArgb(220, 32, 32, 32);

    /// <summary>
    /// The application icon at <see cref="Layout"/>'s size, drawn under everything else.
    /// </summary>
    private readonly Icon _baseIcon;

    /// <summary>
    /// The icons drawn so far and the HICONs behind them.
    /// </summary>
    private readonly Dictionary<TrayIconVisual, (Icon Icon, nint Handle)> _icons = [];

    /// <summary>
    /// Prepares a renderer for one size.
    /// </summary>
    /// <param name="iconBytes">The bytes of the application's <c>.ico</c> file.</param>
    /// <param name="size">The edge length of the icons, in pixels.</param>
    public TrayIconRenderer(byte[] iconBytes, int size)
    {
        Layout = TrayIconLayout.For(size);
        using var stream = new MemoryStream(iconBytes, writable: false);
        _baseIcon = new(stream, size, size);
    }

    /// <summary>
    /// Gets the layout the icons are drawn with.
    /// </summary>
    public TrayIconLayout Layout { get; }

    /// <summary>
    /// Gets the icon for a visual, drawing it the first time.
    /// </summary>
    /// <param name="visual">The lights and badge to show.</param>
    /// <returns>The icon; owned by this renderer.</returns>
    public Icon GetIcon(TrayIconVisual visual)
    {
        if (_icons.TryGetValue(visual, out var cached))
        {
            return cached.Icon;
        }

        var size = Layout.Size;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawIcon(_baseIcon, new Rectangle(0, 0, size, size));

            if (visual.Read)
            {
                DrawLight(graphics, Layout.ReadLight, ReadColor, Layout.Outline);
            }

            if (visual.Write)
            {
                DrawLight(graphics, Layout.WriteLight, WriteColor, Layout.Outline);
            }

            switch (visual.Badge)
            {
                case TrayAlertLevel.Warning:
                    DrawWarningBadge(graphics, Layout.Badge, Layout.Outline);
                    break;
                case TrayAlertLevel.Critical:
                    DrawCriticalBadge(graphics, Layout.Badge, Layout.Outline);
                    break;
            }
        }

        var handle = bitmap.GetHicon();
        var icon = Icon.FromHandle(handle);
        _icons[visual] = (icon, handle);
        return icon;
    }

    /// <summary>
    /// Releases every icon drawn and the base icon.
    /// </summary>
    public void Dispose()
    {
        foreach (var (icon, handle) in _icons.Values)
        {
            icon.Dispose();
            WindowNativeMethods.DestroyIcon(handle);
        }

        _icons.Clear();
        _baseIcon.Dispose();
    }

    /// <summary>
    /// Draws an activity light: a filled dot with a dark outline and a small highlight, like an LED.
    /// </summary>
    /// <param name="graphics">The surface.</param>
    /// <param name="bounds">The light's bounds.</param>
    /// <param name="color">The light's colour.</param>
    /// <param name="outline">The outline width.</param>
    private static void DrawLight(Graphics graphics, Rectangle bounds, Color color, float outline)
    {
        var inset = outline / 2f;
        var dot = new RectangleF(bounds.X + inset, bounds.Y + inset, bounds.Width - outline, bounds.Height - outline);
        using var fill = new SolidBrush(color);
        using var pen = new Pen(OutlineColor, outline);
        graphics.FillEllipse(fill, dot);
        graphics.DrawEllipse(pen, dot);

        // The highlight only helps once there is room for it; at 5 px it would just blur the dot.
        if (bounds.Width >= 8)
        {
            using var highlight = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
            var spot = dot.Width / 3f;
            graphics.FillEllipse(highlight, dot.X + dot.Width * 0.22f, dot.Y + dot.Height * 0.18f, spot, spot);
        }
    }

    /// <summary>
    /// Draws the warning badge: an amber disc with a dark "!".
    /// </summary>
    /// <param name="graphics">The surface.</param>
    /// <param name="bounds">The badge's bounds.</param>
    /// <param name="outline">The outline width.</param>
    private static void DrawWarningBadge(Graphics graphics, Rectangle bounds, float outline)
    {
        var inset = outline / 2f;
        var disc = new RectangleF(bounds.X + inset, bounds.Y + inset, bounds.Width - outline, bounds.Height - outline);
        using var fill = new SolidBrush(WarningColor);
        using var pen = new Pen(OutlineColor, outline);
        using var glyph = new SolidBrush(Color.FromArgb(255, 40, 30, 0));
        graphics.FillEllipse(fill, disc);
        graphics.DrawEllipse(pen, disc);
        DrawExclamation(graphics, bounds, glyph, top: 0.2f, stemEnd: 0.62f, dotTop: 0.70f);
    }

    /// <summary>
    /// Draws the critical badge: a red triangle with a white "!". A different shape from the
    /// warning disc, so the two differ without relying on colour.
    /// </summary>
    /// <param name="graphics">The surface.</param>
    /// <param name="bounds">The badge's bounds.</param>
    /// <param name="outline">The outline width.</param>
    private static void DrawCriticalBadge(Graphics graphics, Rectangle bounds, float outline)
    {
        var inset = outline / 2f;
        PointF[] triangle =
        [
            new(bounds.Left + bounds.Width / 2f, bounds.Top + inset),
            new(bounds.Right - inset, bounds.Bottom - inset),
            new(bounds.Left + inset, bounds.Bottom - inset),
        ];
        using var fill = new SolidBrush(CriticalColor);
        using var pen = new Pen(OutlineColor, outline) { LineJoin = LineJoin.Round };
        using var glyph = new SolidBrush(Color.White);
        graphics.FillPolygon(fill, triangle);
        graphics.DrawPolygon(pen, triangle);
        DrawExclamation(graphics, bounds, glyph, top: 0.34f, stemEnd: 0.70f, dotTop: 0.76f);
    }

    /// <summary>
    /// Draws a "!" from two shapes rather than text, so it does not depend on font hinting at this size.
    /// </summary>
    /// <param name="graphics">The surface.</param>
    /// <param name="bounds">The badge's bounds.</param>
    /// <param name="brush">The glyph brush.</param>
    /// <param name="top">Where the stem starts, as a fraction of the height.</param>
    /// <param name="stemEnd">Where the stem ends, as a fraction of the height.</param>
    /// <param name="dotTop">Where the dot starts, as a fraction of the height.</param>
    private static void DrawExclamation(Graphics graphics, Rectangle bounds, Brush brush, float top, float stemEnd, float dotTop)
    {
        var width = Math.Max(1.5f, bounds.Width / 6f);
        var left = bounds.Left + (bounds.Width - width) / 2f;
        graphics.FillRectangle(brush, left, bounds.Top + bounds.Height * top, width, bounds.Height * (stemEnd - top));
        graphics.FillEllipse(brush, left, bounds.Top + bounds.Height * dotTop, width, width);
    }
}
