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
        if (isDark)
        {
            ToolStripDropDownBackground = Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A);
            MenuBorder = Color.FromArgb(0xFF, 0x3D, 0x3D, 0x3D);
            MenuItemSelected = Color.FromArgb(0xFF, 0x3A, 0x3A, 0x50);
            SeparatorDark = Color.FromArgb(0xFF, 0x44, 0x44, 0x44);
        }
        else
        {
            ToolStripDropDownBackground = Color.White;
            MenuBorder = Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0);
            MenuItemSelected = Color.FromArgb(0xFF, 0xE8, 0xEA, 0xF6);
            SeparatorDark = Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0);
        }
    }

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
    private readonly Color _foreground = isDark ? Color.White : Color.Black;
    private readonly Color _hoverBackground = isDark
        ? Color.FromArgb(0xFF, 0x3A, 0x3A, 0x50)
        : Color.FromArgb(0xFF, 0xE8, 0xEA, 0xF6);

    /// <summary>
    /// Renders item text in the theme's foreground color, or the system gray when the item is disabled.
    /// </summary>
    /// <summary>
    /// Renders item text in the theme's foreground color, or the system gray when the item is disabled.
    /// </summary>
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _foreground : System.Drawing.SystemColors.GrayText;
        base.OnRenderItemText(e);
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
