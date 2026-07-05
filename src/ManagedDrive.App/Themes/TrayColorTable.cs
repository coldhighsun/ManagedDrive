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
