namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// The application-level dialogs that tray and toolbar actions open (settings, about). They share
/// one gate, so at most one of them is open at a time: asking for another brings the open one
/// forward instead of stacking a second modal loop on top of it. The exit confirmation has its own
/// gate and takes precedence over both.
/// </summary>
internal static class AppDialogs
{
    /// <summary>
    /// Gets the gate shared by the settings and about dialogs.
    /// </summary>
    public static SingleDialogGate<Window> Shared { get; } = new();

    /// <summary>
    /// Gets the gate of the exit confirmation.
    /// </summary>
    public static SingleDialogGate<ConfirmDialog> ExitConfirm { get; } = new();

    /// <summary>
    /// Opens an application dialog modally, unless the application is exiting, the exit
    /// confirmation is open, or another application dialog is already open (that one, or the
    /// confirmation, is then brought forward instead).
    /// </summary>
    /// <typeparam name="TDialog">The dialog type.</typeparam>
    /// <param name="create">Creates the dialog; only called when it is going to be shown.</param>
    /// <param name="mainWindow">The main window, which owns the dialog only while it is shown.</param>
    /// <param name="isExiting">Whether the application is shutting down.</param>
    /// <returns>The dialog result, or <see langword="null"/> if no dialog was shown.</returns>
    public static bool? ShowModal<TDialog>(Func<TDialog> create, Window? mainWindow, bool isExiting)
        where TDialog : Window
    {
        switch (AppDialogDecision.Decide(isExiting, ExitConfirm.Current is not null, Shared.Current is not null))
        {
            case AppDialogAction.Skip:
                return null;
            case AppDialogAction.BringExitConfirmForward:
                ExitConfirm.TryBringForward(DialogPlacement.BringForward);
                return null;
            case AppDialogAction.BringOpenForward:
                Shared.TryBringForward(DialogPlacement.BringForward);
                return null;
        }

        ReleaseSplashTopmost();

        var dialog = create();
        var owner = GetOwner(mainWindow);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        dialog.WindowStartupLocation = WindowVisibility.GetStartupLocation(owner is not null);
        return Shared.Show(dialog, d => d.ShowDialog());
    }

    /// <summary>
    /// Lets go of the startup splash's topmost state, if it is up, so a dialog opened now does not
    /// end up under it.
    /// </summary>
    public static void ReleaseSplashTopmost() =>
        Application.Current?.Windows.OfType<SplashWindow>().FirstOrDefault()?.ReleaseTopmost();

    /// <summary>
    /// Closes the exit confirmation and the settings or about dialog that are open, if any, so
    /// they return without a result. The confirmation goes first because it may sit on top of the
    /// other dialog.
    /// </summary>
    /// <returns><see langword="true"/> if at least one dialog was open and was closed.</returns>
    public static bool CloseOpenDialog()
    {
        var closedConfirm = ExitConfirm.TryClose(d => d.Close());
        var closedShared = Shared.TryClose(d => d.Close());
        return closedConfirm || closedShared;
    }

    /// <summary>
    /// Opens the about dialog, or brings the application dialog that is already open forward.
    /// </summary>
    /// <param name="updateCheckService">The update service the dialog reports on, if any.</param>
    /// <param name="mainWindow">The main window, which owns the dialog only while it is shown.</param>
    /// <param name="isExiting">Whether the application is shutting down.</param>
    public static void ShowAbout(UpdateCheckService? updateCheckService, Window? mainWindow, bool isExiting) =>
        ShowModal(() => new AboutDialog(updateCheckService), mainWindow, isExiting);

    /// <summary>
    /// Picks the owner for a dialog: the main window when it is shown, and never the splash, whose
    /// closing would take its owned dialogs with it.
    /// </summary>
    /// <param name="mainWindow">The window that is the application's main window right now.</param>
    /// <returns>The owner, or <see langword="null"/> for an ownerless dialog.</returns>
    public static Window? GetOwner(Window? mainWindow) =>
        WindowVisibility.IsOwnerCandidate(WindowVisibility.IsShownToUser(mainWindow), mainWindow is SplashWindow)
            ? mainWindow
            : null;
}
