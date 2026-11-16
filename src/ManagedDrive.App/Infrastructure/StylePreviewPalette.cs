using Color = System.Windows.Media.Color;

namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// The few colours a miniature preview of a visual style needs, taken from that style's own palette.
/// </summary>
/// <param name="Background">The window background.</param>
/// <param name="Surface">The card surface.</param>
/// <param name="Sidebar">The sidebar background.</param>
/// <param name="Divider">Borders and hairlines.</param>
/// <param name="Track">The empty part of a usage bar.</param>
/// <param name="Primary">The accent colour.</param>
/// <param name="Fill">The filled part of a usage bar.</param>
/// <param name="Text">Strong text (drawn as bars in the preview).</param>
/// <param name="Muted">Weak text.</param>
/// <param name="Accents">The colours of the toolbar icons and drive-letter badges.</param>
public sealed record StylePreviewColors(
    Color Background,
    Color Surface,
    Color Sidebar,
    Color Divider,
    Color Track,
    Color Primary,
    Color Fill,
    Color Text,
    Color Muted,
    IReadOnlyList<Color> Accents);

/// <summary>
/// Pure lookup of the preview colours of a visual style in the light or dark theme. The values are copied from
/// <c>AppStyle.{B,C}.Colors.{Light,Dark}.xaml</c> (and the shared success colour), so a preview looks like the style
/// it stands for without having to apply it.
/// </summary>
public static class StylePreviewPalette
{
    private static readonly StylePreviewColors DashboardLight = new(
        Rgb(0xF2F4F8), Rgb(0xFFFFFF), Rgb(0xFFFFFF), Rgb(0xC5CCD9), Rgb(0xD8DDE8), Rgb(0x2B6CF0), Rgb(0x1E9E5A),
        Rgb(0x1B1E27), Rgb(0x5F6676), [Rgb(0x2B6CF0), Rgb(0x00A38C), Rgb(0x8E44D6), Rgb(0xEA6F0C)]);

    private static readonly StylePreviewColors DashboardDark = new(
        Rgb(0x12141A), Rgb(0x212530), Rgb(0x0D0F14), Rgb(0x363C4B), Rgb(0x363C4B), Rgb(0x4F8DF7), Rgb(0x43C77D),
        Rgb(0xE7E9EE), Rgb(0x8D95A6), [Rgb(0x4F8DF7), Rgb(0x2DD4BF), Rgb(0xB388F5), Rgb(0xFF9A4D)]);

    private static readonly StylePreviewColors MinimalLight = new(
        Rgb(0xFAFAF8), Rgb(0xFFFFFF), Rgb(0xFAFAF8), Rgb(0xC9C9C2), Rgb(0xD9D9D3), Rgb(0x1C1C1C), Rgb(0x1C1C1C),
        Rgb(0x1C1C1C), Rgb(0x6B6B66), [Rgb(0x1C1C1C), Rgb(0x1C1C1C), Rgb(0x1C1C1C), Rgb(0x1C1C1C)]);

    private static readonly StylePreviewColors MinimalDark = new(
        Rgb(0x121212), Rgb(0x1A1A1A), Rgb(0x121212), Rgb(0x3A3A3A), Rgb(0x3A3A3A), Rgb(0xF2F2F2), Rgb(0xF2F2F2),
        Rgb(0xEDEDED), Rgb(0x9A9A94), [Rgb(0xEDEDED), Rgb(0xEDEDED), Rgb(0xEDEDED), Rgb(0xEDEDED)]);

    /// <summary>
    /// Whether a style tag stands for the minimal style; anything else, including <c>null</c>, is the dashboard
    /// (the default), as <see cref="StyleManager.Resolve"/> treats an unknown tag.
    /// </summary>
    /// <param name="style">A style tag such as <c>b</c> or <c>c</c>.</param>
    /// <returns><c>true</c> for the minimal style.</returns>
    public static bool IsMinimal(string? style) => string.Equals(style, "c", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the preview colours of a style in a theme.
    /// </summary>
    /// <param name="style">A style tag such as <c>b</c> or <c>c</c>; unknown or <c>null</c> means the dashboard.</param>
    /// <param name="dark"><c>true</c> for the dark theme.</param>
    /// <returns>The colours.</returns>
    public static StylePreviewColors For(string? style, bool dark) => (IsMinimal(style), dark) switch
    {
        (false, false) => DashboardLight,
        (false, true) => DashboardDark,
        (true, false) => MinimalLight,
        (true, true) => MinimalDark,
    };

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
