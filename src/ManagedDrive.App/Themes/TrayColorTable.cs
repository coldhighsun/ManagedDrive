using System.Windows.Forms;

namespace ManagedDrive.App.Themes;

/// <summary>
/// Supplies dark/light colors for the tray icon's <see cref="ContextMenuStrip"/>. WinForms
/// controls don't participate in WPF's DynamicResource theme switching, so these colors are
/// applied manually whenever <see cref="ThemeManager"/> resolves a new theme.
/// </summary>
public sealed class TrayColorTable : ProfessionalColorTable
{
    private readonly Color _background;
    private readonly Color _border;
    private readonly Color _hover;
    private readonly Color _separator;

    public TrayColorTable(bool isDark)
    {
        if (isDark)
        {
            _background = Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A);
            _border = Color.FromArgb(0xFF, 0x3D, 0x3D, 0x3D);
            _hover = Color.FromArgb(0xFF, 0x3A, 0x3A, 0x50);
            _separator = Color.FromArgb(0xFF, 0x44, 0x44, 0x44);
        }
        else
        {
            _background = Color.White;
            _border = Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0);
            _hover = Color.FromArgb(0xFF, 0xE8, 0xEA, 0xF6);
            _separator = Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0);
        }
    }

    public override Color ToolStripDropDownBackground => _background;

    public override Color ImageMarginGradientBegin => _background;

    public override Color ImageMarginGradientMiddle => _background;

    public override Color ImageMarginGradientEnd => _background;

    public override Color MenuBorder => _border;

    public override Color MenuItemBorder => _border;

    public override Color MenuItemSelected => _hover;

    public override Color MenuItemSelectedGradientBegin => _hover;

    public override Color MenuItemSelectedGradientEnd => _hover;

    public override Color SeparatorDark => _separator;

    public override Color SeparatorLight => _separator;
}
