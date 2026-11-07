using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ManagedDrive.App.Themes;

/// <summary>
/// Supplies dark/light colors for the tray icon's <see cref="ContextMenuStrip"/>. WinForms
/// controls don't participate in WPF's DynamicResource theme switching, so these colors are
/// applied manually whenever <see cref="ThemeManager"/> resolves a new theme.
/// </summary>
public sealed class TrayColorTable : ProfessionalColorTable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TrayColorTable"/> class from <paramref name="palette"/>, or from
    /// the plain dark or light palette (<see cref="TrayMenuPalette.GetDefault"/>) when none is given.
    /// </summary>
    /// <param name="isDark">Whether the dark palette is the fallback.</param>
    /// <param name="palette">The colors to use, normally those of the current style and theme.</param>
    public TrayColorTable(bool isDark, TrayMenuPalette? palette = null)
    {
        var colors = palette ?? TrayMenuPalette.GetDefault(isDark);
        MenuBorder = colors.Border;
        ToolStripDropDownBackground = colors.Background;
        MenuItemSelected = colors.Hover;
        SeparatorDark = colors.Separator;
    }

    /// <summary>
    /// Gets the menu border color of a palette. The single source of this color: the table's own
    /// <see cref="MenuBorder"/> and the border DWM draws around the rounded popup both use it.
    /// </summary>
    /// <param name="isDark">Whether the dark palette is wanted.</param>
    /// <returns>The border color.</returns>
    public static Color GetMenuBorder(bool isDark) =>
        isDark ? Color.FromArgb(0xFF, 0x3D, 0x3D, 0x3D) : Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0);

    /// <summary>
    /// Gets the background color of the drop-down menu.
    /// </summary>
    public override Color ToolStripDropDownBackground { get; }

    /// <summary>
    /// Gets the start color of the image margin gradient; matches the menu background.
    /// </summary>
    public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;

    /// <summary>
    /// Gets the middle color of the image margin gradient; matches the menu background.
    /// </summary>
    public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;

    /// <summary>
    /// Gets the end color of the image margin gradient; matches the menu background.
    /// </summary>
    public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;

    /// <summary>
    /// Gets the color of the menu border.
    /// </summary>
    public override Color MenuBorder { get; }

    /// <summary>
    /// Gets the border color of a selected menu item; matches the menu border.
    /// </summary>
    public override Color MenuItemBorder => MenuBorder;

    /// <summary>
    /// Gets the background color of a selected menu item.
    /// </summary>
    public override Color MenuItemSelected { get; }

    /// <summary>
    /// Gets the start color of the selected item gradient; matches the selected color.
    /// </summary>
    public override Color MenuItemSelectedGradientBegin => MenuItemSelected;

    /// <summary>
    /// Gets the end color of the selected item gradient; matches the selected color.
    /// </summary>
    public override Color MenuItemSelectedGradientEnd => MenuItemSelected;

    /// <summary>
    /// Gets the dark color of menu separators.
    /// </summary>
    public override Color SeparatorDark { get; }

    /// <summary>
    /// Gets the light color of menu separators; matches the dark color so separators render flat.
    /// </summary>
    public override Color SeparatorLight => SeparatorDark;
}

/// <summary>
/// Pairs with <see cref="TrayColorTable"/> to render the tray icon's <see cref="ContextMenuStrip"/>.
/// Forces the hovered/selected item's text color explicitly instead of relying on
/// <see cref="ToolStripItem.ForeColor"/> alone — on Windows 11, the base
/// <see cref="ToolStripProfessionalRenderer"/> can pick up the OS's own highlight-text color for a
/// selected item regardless of <c>ForeColor</c>, which produces unreadable white-on-white text
/// when the tray menu is in light mode.
/// </summary>
public sealed class TrayMenuRenderer(bool isDark, TrayMenuPalette? palette = null)
    : ToolStripProfessionalRenderer(new TrayColorTable(isDark, palette))
{
    /// <summary>
    /// The opacity of the submenu arrow (0-255) against the foreground color, which mutes it.
    /// </summary>
    private const int ArrowAlpha = 0xB4;

    /// <summary>
    /// The stroke width of the submenu arrow at 96 DPI, in pixels.
    /// </summary>
    private const float ArrowStrokeWidth = 1.4f;

    /// <summary>
    /// Half the height of the submenu arrow at 96 DPI, in pixels.
    /// </summary>
    private const float ArrowHalfHeight = 3.5f;

    /// <summary>
    /// How far the tip of the submenu arrow lies right of the center of its rectangle at 96 DPI, in pixels.
    /// </summary>
    private const float ArrowTipOffset = 2f;

    /// <summary>
    /// How far the two ends of the submenu arrow lie left of the center of its rectangle at 96 DPI, in pixels.
    /// </summary>
    private const float ArrowEndOffset = 1.5f;

    private readonly TrayMenuPalette _palette = palette ?? TrayMenuPalette.GetDefault(isDark);

    private Color _foreground => _palette.Foreground;

    private Color _hoverBackground => _palette.Hover;

    /// <summary>
    /// Renders item text in the theme's foreground color, or the system gray when the item is
    /// disabled. A menu item's text is also centered vertically (<see cref="CenterVertically"/>):
    /// WinForms lays it out at the top of an item that is taller than its text, whatever makes it
    /// taller, and the submenu arrow, which is centered, would then sit lower than the text.
    /// </summary>
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _foreground : _palette.Muted;
        if (e.Item is ToolStripMenuItem)
        {
            e.TextRectangle = CenterVertically(e.TextRectangle, e.Item.Height);
        }

        base.OnRenderItemText(e);
    }

    /// <summary>
    /// Draws the submenu arrow as a thin chevron in a muted shade of the foreground color, centered
    /// on the item, instead of the base renderer's black filled triangle. It is scaled with the DPI
    /// of the menu that owns the item, the same DPI the item padding is scaled with.
    /// </summary>
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        var color = e.Item?.Enabled == false ? _palette.Muted : Color.FromArgb(ArrowAlpha, _foreground);
        var scale = (e.Item?.Owner?.DeviceDpi ?? (int)e.Graphics.DpiY) / 96f;
        using var pen = new Pen(color, ArrowStrokeWidth * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, GetChevron(e.ArrowRectangle, scale));
    }

    /// <summary>
    /// Moves a text rectangle so that it is centered vertically in an item of the given height.
    /// </summary>
    /// <param name="textBounds">The text rectangle WinForms laid out, in item coordinates.</param>
    /// <param name="itemHeight">The height of the item.</param>
    /// <returns>The same rectangle, centered vertically.</returns>
    internal static Rectangle CenterVertically(Rectangle textBounds, int itemHeight) =>
        new(textBounds.X, (itemHeight - textBounds.Height) / 2, textBounds.Width, textBounds.Height);

    /// <summary>
    /// Gets the three points of a right-pointing chevron centered in the arrow rectangle.
    /// </summary>
    /// <param name="arrowBounds">The rectangle WinForms reserves for the arrow, in item coordinates.</param>
    /// <param name="scale">The DPI scale, 1 at 96 DPI.</param>
    /// <returns>The top, tip and bottom point of the chevron.</returns>
    internal static PointF[] GetChevron(Rectangle arrowBounds, float scale)
    {
        var centerX = arrowBounds.Left + arrowBounds.Width / 2f;
        var centerY = arrowBounds.Top + arrowBounds.Height / 2f;
        return
        [
            new(centerX - ArrowEndOffset * scale, centerY - ArrowHalfHeight * scale),
            new(centerX + ArrowTipOffset * scale, centerY),
            new(centerX - ArrowEndOffset * scale, centerY + ArrowHalfHeight * scale),
        ];
    }

    /// <summary>
    /// Draws the square border only where the popup is not rounded. Where DWM rounds the popup
    /// (Windows 11) it draws the border itself, in <see cref="TrayColorTable.GetMenuBorder"/>'s
    /// color, and a square border painted here would be cut off at the corners; Windows 10 has no
    /// such border, so the menu would have none.
    /// </summary>
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (!WindowCornerHelper.IsSupportedByThisOs)
        {
            base.OnRenderToolStripBorder(e);
        }
    }

    /// <summary>
    /// Fills the hovered/selected item's background directly instead of delegating to the base
    /// renderer's gradient painting from <see cref="TrayColorTable"/> — on Windows 11 that gradient
    /// path can be preempted by the OS's own (light-colored) hot-track highlight for a menu popup,
    /// which combined with the white hover text produces unreadable white-on-white.
    /// </summary>
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Selected || e.Item.Pressed)
        {
            using var brush = new SolidBrush(_hoverBackground);
            e.Graphics.FillRectangle(brush, new(System.Drawing.Point.Empty, e.Item.Size));
            return;
        }

        base.OnRenderMenuItemBackground(e);
    }
}

/// <summary>
/// The colors of the tray icon's menu: those of the current style and theme when the application's resources can be
/// read (<see cref="FromResources"/>), otherwise the plain dark or light set (<see cref="GetDefault"/>).
/// </summary>
/// <param name="Background">The menu background.</param>
/// <param name="Hover">The background of the item under the mouse, already blended over the background.</param>
/// <param name="Separator">The color of separators.</param>
/// <param name="Border">The border of the menu.</param>
/// <param name="Foreground">The text color.</param>
/// <param name="Muted">The text color of a disabled item.</param>
public readonly record struct TrayMenuPalette(Color Background, Color Hover, Color Separator, Color Border, Color Foreground, Color Muted)
{
    /// <summary>
    /// Gets the plain palette of a theme, used when the application's resources are not available.
    /// </summary>
    /// <param name="isDark">Whether the dark palette is wanted.</param>
    /// <returns>The palette.</returns>
    public static TrayMenuPalette GetDefault(bool isDark) => isDark
        ? new(
            Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A),
            Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A),
            Color.FromArgb(0xFF, 0x44, 0x44, 0x44),
            TrayColorTable.GetMenuBorder(true),
            Color.White,
            Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A))
        : new(
            Color.White,
            Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE),
            Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0),
            TrayColorTable.GetMenuBorder(false),
            Color.Black,
            Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A));

    /// <summary>
    /// Reads the palette from the application's resources, so the menu follows the visual style and the theme; a
    /// color whose resource is missing falls back to <see cref="GetDefault"/>. Call it on the UI thread.
    /// </summary>
    /// <param name="isDark">Whether the dark palette is the fallback.</param>
    /// <returns>The palette.</returns>
    public static TrayMenuPalette FromResources(bool isDark)
    {
        var fallback = GetDefault(isDark);
        var background = Read("AppSurface") ?? fallback.Background;
        var divider = Read("AppDivider");
        return new(
            background,
            Read("AppHover") is { } tint ? Blend(tint, background) : fallback.Hover,
            divider is { } line ? Blend(line, background) : fallback.Separator,
            divider is { } edge ? Blend(edge, background) : fallback.Border,
            Read("AppForeground") is { } foreground ? Blend(foreground, background) : fallback.Foreground,
            Read("AppForegroundMuted") is { } muted ? Blend(muted, background) : fallback.Muted);
    }

    private static Color? Read(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush
            ? Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B)
            : null;

    // GDI+ menus draw opaque rectangles, so a translucent tint (a hover wash, say) is laid over the background here.
    internal static Color Blend(Color top, Color under)
    {
        var alpha = top.A / 255.0;
        return Color.FromArgb(
            0xFF,
            (int)Math.Round((top.R * alpha) + (under.R * (1 - alpha))),
            (int)Math.Round((top.G * alpha) + (under.G * (1 - alpha))),
            (int)Math.Round((top.B * alpha) + (under.B * (1 - alpha))));
    }
}
