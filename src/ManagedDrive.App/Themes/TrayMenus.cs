using System.Windows.Forms;

namespace ManagedDrive.App.Themes;

/// <summary>
/// The measures of the tray menus' side room. A drop-down menu ignores the horizontal padding of its
/// items, so the room between the edge of the menu and the text is made with the margin of the items
/// (<see cref="GetItemMargin"/>), which moves the text, the submenu arrow and the highlight in. The menu
/// does not count that margin in its width, so <see cref="TrayContextMenu"/> and
/// <see cref="TraySubmenu"/> ask for it themselves (<see cref="GetMenuWidth"/>).
/// </summary>
public static class TrayMenuMetrics
{
    private const int DefaultDpi = 96;

    /// <summary>
    /// The space between the edge of the menu and the text, on each side, at 96 DPI, in pixels.
    /// </summary>
    private const int SideIndent = 8;

    /// <summary>
    /// How much narrower than WinForms asks, at 96 DPI in pixels, a submenu none of whose items opens a
    /// submenu is: every item gets room for a submenu arrow at the right, which such a submenu never
    /// draws. Only part of that room is given back, so a long text is never cut off.
    /// </summary>
    private const int LeafArrowTrim = 20;

    /// <summary>
    /// Gets the space between the edge of the menu and the text of an item, on each side.
    /// </summary>
    /// <param name="deviceDpi">The DPI of the menu.</param>
    /// <returns>The indent in device pixels.</returns>
    internal static int GetIndent(int deviceDpi) => Scale(SideIndent, deviceDpi);

    /// <summary>
    /// Gets the margin of a menu item: the indent on the left and right, nothing above or below.
    /// </summary>
    /// <param name="deviceDpi">The DPI of the menu.</param>
    /// <returns>The margin in device pixels.</returns>
    internal static Padding GetItemMargin(int deviceDpi)
    {
        var indent = GetIndent(deviceDpi);
        return new(indent, 0, indent, 0);
    }

    /// <summary>
    /// Gets how much narrower a submenu without submenus of its own is made.
    /// </summary>
    /// <param name="deviceDpi">The DPI of the menu.</param>
    /// <returns>The trim in device pixels.</returns>
    internal static int GetLeafArrowTrim(int deviceDpi) => Scale(LeafArrowTrim, deviceDpi);

    /// <summary>
    /// Gets the width a menu asks for: the width WinForms asks for plus the indent on both sides.
    /// </summary>
    /// <param name="baseWidth">The width WinForms asks for.</param>
    /// <param name="deviceDpi">The DPI of the menu.</param>
    /// <returns>The width the menu asks for.</returns>
    internal static int GetMenuWidth(int baseWidth, int deviceDpi) => baseWidth + (2 * GetIndent(deviceDpi));

    private static int Scale(int pixels, int deviceDpi) => (int)Math.Round(pixels * deviceDpi / (double)DefaultDpi);
}

/// <summary>
/// The context menu of the tray icon. It is as wide as its items need plus the side room of
/// <see cref="TrayMenuMetrics"/>; the menu sizes itself from its items alone, so it has to be asked.
/// </summary>
public sealed class TrayContextMenu : ContextMenuStrip
{
    /// <summary>
    /// Gets the size the menu asks for.
    /// </summary>
    /// <param name="proposedSize">The size the menu has to fit in.</param>
    /// <returns>The preferred size.</returns>
    public override System.Drawing.Size GetPreferredSize(System.Drawing.Size proposedSize)
    {
        var size = base.GetPreferredSize(proposedSize);
        return new(TrayMenuMetrics.GetMenuWidth(size.Width, DeviceDpi), size.Height);
    }
}

/// <summary>
/// A submenu of the tray icon's menu. Like <see cref="TrayContextMenu"/> it is wider by the side room,
/// and when none of its items opens a submenu it also gives back the room reserved for an arrow.
/// </summary>
public sealed class TraySubmenu : ToolStripDropDownMenu
{
    /// <summary>
    /// Gets the size the submenu asks for.
    /// </summary>
    /// <param name="proposedSize">The size the submenu has to fit in.</param>
    /// <returns>The preferred size.</returns>
    public override System.Drawing.Size GetPreferredSize(System.Drawing.Size proposedSize)
    {
        var size = base.GetPreferredSize(proposedSize);
        var width = TrayMenuMetrics.GetMenuWidth(size.Width, DeviceDpi);
        if (!Items.OfType<ToolStripMenuItem>().Any(item => item.HasDropDownItems))
        {
            width -= TrayMenuMetrics.GetLeafArrowTrim(DeviceDpi);
        }

        return new(width, size.Height);
    }
}
