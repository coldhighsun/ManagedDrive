using System.Windows.Forms;

namespace ManagedDrive.App.Themes;

/// <summary>
/// Supplies dark/light colors for the tray icon's <see cref="ContextMenuStrip"/>. WinForms
/// controls don't participate in WPF's DynamicResource theme switching, so these colors are
/// applied manually whenever <see cref="ThemeManager"/> resolves a new theme.
/// </summary>
public sealed class TrayColorTable : ProfessionalColorTable
{
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

    public override Color ToolStripDropDownBackground { get; }

    public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;

    public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;

    public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;

    public override Color MenuBorder { get; }

    public override Color MenuItemBorder => MenuBorder;

    public override Color MenuItemSelected { get; }

    public override Color MenuItemSelectedGradientBegin => MenuItemSelected;

    public override Color MenuItemSelectedGradientEnd => MenuItemSelected;

    public override Color SeparatorDark { get; }

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
