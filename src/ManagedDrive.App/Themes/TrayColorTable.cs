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
    /// Initializes a new instance of the <see cref="TrayColorTable"/> class using the dark palette when
    /// <c>isDark</c> is <c>true</c> and the light palette otherwise.
    /// </summary>
    public TrayColorTable(bool isDark)
    {
        MenuBorder = GetMenuBorder(isDark);
        if (isDark)
        {
            ToolStripDropDownBackground = Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A);
            MenuItemSelected = Color.FromArgb(0xFF, 0x3A, 0x3A, 0x50);
            SeparatorDark = Color.FromArgb(0xFF, 0x44, 0x44, 0x44);
        }
        else
        {
            ToolStripDropDownBackground = Color.White;
            MenuItemSelected = Color.FromArgb(0xFF, 0xE8, 0xEA, 0xF6);
            SeparatorDark = Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0);
        }
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
public sealed class TrayMenuRenderer(bool isDark) : ToolStripProfessionalRenderer(new TrayColorTable(isDark))
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

    private readonly Color _foreground = isDark ? Color.White : Color.Black;
    private readonly Color _hoverBackground = isDark
        ? Color.FromArgb(0xFF, 0x3A, 0x3A, 0x50)
        : Color.FromArgb(0xFF, 0xE8, 0xEA, 0xF6);

    /// <summary>
    /// Renders item text in the theme's foreground color, or the system gray when the item is
    /// disabled. A menu item's text is also centered vertically (<see cref="CenterVertically"/>):
    /// WinForms lays it out at the top of an item that is taller than its text, whatever makes it
    /// taller, and the submenu arrow, which is centered, would then sit lower than the text.
    /// </summary>
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _foreground : System.Drawing.SystemColors.GrayText;
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
        var color = e.Item?.Enabled == false ? System.Drawing.SystemColors.GrayText : Color.FromArgb(ArrowAlpha, _foreground);
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
