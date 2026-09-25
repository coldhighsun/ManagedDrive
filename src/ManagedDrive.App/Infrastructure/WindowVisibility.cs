namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Decides whether a window's content is actually in front of the user. A window minimized to
/// the taskbar still reports <see cref="UIElement.IsVisible"/> as <see langword="true"/>, yet its
/// status bar can't be seen, so notifications must reach the user another way (a tray balloon).
/// </summary>
internal static class WindowVisibility
{
    /// <summary>
    /// Returns whether a window with the given state shows its content to the user.
    /// </summary>
    /// <param name="isVisible">The window's <see cref="UIElement.IsVisible"/>.</param>
    /// <param name="state">The window's <see cref="Window.WindowState"/>.</param>
    /// <returns><see langword="true"/> if the window is shown and not minimized.</returns>
    public static bool IsShownToUser(bool isVisible, WindowState state) =>
        isVisible && state != WindowState.Minimized;

    /// <summary>
    /// Returns whether <paramref name="window"/> shows its content to the user.
    /// </summary>
    /// <param name="window">The window to check, or <see langword="null"/> if there is none.</param>
    /// <returns><see langword="true"/> if the window exists, is shown and is not minimized.</returns>
    public static bool IsShownToUser(Window? window) =>
        window is not null && IsShownToUser(window.IsVisible, window.WindowState);
}
