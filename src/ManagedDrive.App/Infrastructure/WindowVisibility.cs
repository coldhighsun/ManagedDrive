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

    /// <summary>
    /// Picks where a dialog opens: centered on its owner when it has one, otherwise on the screen.
    /// </summary>
    /// <param name="hasOwner">Whether the dialog has a shown owner window.</param>
    /// <returns>The startup location for the dialog.</returns>
    public static WindowStartupLocation GetStartupLocation(bool hasOwner) =>
        hasOwner ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

    /// <summary>
    /// Picks the owner for a dialog that must not be blocked by another one: the application
    /// dialog already open if there is one and it is shown to the user (so the new dialog stacks on
    /// top of it), otherwise the main window when it is shown, otherwise none. A minimized dialog
    /// is skipped: a window owned by a minimized window is hidden with it, which would leave the
    /// new dialog unreachable behind its own modal loop.
    /// </summary>
    /// <typeparam name="TWindow">The window type.</typeparam>
    /// <param name="activeDialog">The application dialog that is open now, if any.</param>
    /// <param name="isActiveDialogShown">Whether <paramref name="activeDialog"/> is shown and not minimized.</param>
    /// <param name="shownMainWindow">The main window if it is shown to the user, otherwise <see langword="null"/>.</param>
    /// <returns>The owner, or <see langword="null"/> for an ownerless dialog.</returns>
    public static TWindow? PickOwner<TWindow>(TWindow? activeDialog, bool isActiveDialogShown, TWindow? shownMainWindow)
        where TWindow : class =>
        activeDialog is not null && isActiveDialogShown ? activeDialog : shownMainWindow;

    /// <summary>
    /// Returns whether a window can own a dialog: it must be shown and must not be the splash,
    /// which closes while the dialog may still be open.
    /// </summary>
    /// <param name="isShown">Whether the window is shown to the user.</param>
    /// <param name="isSplash">Whether the window is the startup splash.</param>
    /// <returns><see langword="true"/> if the window may own the dialog.</returns>
    public static bool IsOwnerCandidate(bool isShown, bool isSplash) => isShown && !isSplash;
}
