namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// What a request to open an application dialog (settings, about) should do.
/// </summary>
internal enum AppDialogAction
{
    /// <summary>
    /// Open a new dialog.
    /// </summary>
    Open,

    /// <summary>
    /// A settings or about dialog is already open: bring it forward.
    /// </summary>
    BringOpenForward,

    /// <summary>
    /// The exit confirmation is open and blocks every other dialog: bring it forward.
    /// </summary>
    BringExitConfirmForward,

    /// <summary>
    /// Do nothing, the application is shutting down.
    /// </summary>
    Skip
}

/// <summary>
/// The pure rule behind <see cref="AppDialogs.ShowModal{TDialog}"/>, kept apart so it can be tested
/// without windows.
/// </summary>
internal static class AppDialogDecision
{
    /// <summary>
    /// Decides what a request to open an application dialog does.
    /// </summary>
    /// <param name="isExiting">Whether the application is shutting down.</param>
    /// <param name="exitConfirmOpen">Whether the exit confirmation is open.</param>
    /// <param name="dialogOpen">Whether a settings or about dialog is open.</param>
    /// <returns>The action to take.</returns>
    public static AppDialogAction Decide(bool isExiting, bool exitConfirmOpen, bool dialogOpen)
    {
        if (isExiting)
        {
            return AppDialogAction.Skip;
        }

        if (exitConfirmOpen)
        {
            return AppDialogAction.BringExitConfirmForward;
        }

        return dialogOpen ? AppDialogAction.BringOpenForward : AppDialogAction.Open;
    }
}
