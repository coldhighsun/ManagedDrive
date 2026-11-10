namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Keeps at most one instance of a modal dialog open: a second request brings the open one forward
/// instead of stacking another. Like the dialogs it tracks, it is only for use on the UI thread.
/// </summary>
/// <typeparam name="TDialog">The dialog type.</typeparam>
internal sealed class SingleDialogGate<TDialog>
    where TDialog : class
{
    /// <summary>
    /// The dialogs that are open right now, oldest first. Normally at most one; a second entry only
    /// appears if a request slips past the caller's check from a nested message loop, and tracking
    /// it keeps the outer dialog findable once the inner one closes.
    /// </summary>
    private readonly List<TDialog> _open = [];

    /// <summary>
    /// Gets the dialog that is open right now (the most recently shown one), or
    /// <see langword="null"/> if there is none.
    /// </summary>
    public TDialog? Current => _open.Count > 0 ? _open[^1] : null;

    /// <summary>
    /// Brings the open dialog forward, if there is one.
    /// </summary>
    /// <param name="bringForward">Restores and activates the dialog.</param>
    /// <returns><see langword="true"/> if a dialog was open and was brought forward.</returns>
    public bool TryBringForward(Action<TDialog> bringForward)
    {
        if (Current is not { } existing)
        {
            return false;
        }

        bringForward(existing);
        return true;
    }

    /// <summary>
    /// Closes the open dialog, if there is one.
    /// </summary>
    /// <param name="close">Closes the dialog.</param>
    /// <returns><see langword="true"/> if a dialog was open and <paramref name="close"/> was called.</returns>
    public bool TryClose(Action<TDialog> close)
    {
        if (Current is not { } existing)
        {
            return false;
        }

        close(existing);
        return true;
    }

    /// <summary>
    /// Shows the dialog and tracks it as the open one until it closes, even if showing throws.
    /// </summary>
    /// <param name="dialog">The dialog to show.</param>
    /// <param name="show">Shows the dialog modally and returns its result.</param>
    /// <returns>The dialog result.</returns>
    public bool? Show(TDialog dialog, Func<TDialog, bool?> show)
    {
        // Set right before showing, so a failure while preparing the dialog cannot leave one that
        // never opened blocking every later request.
        _open.Add(dialog);
        try
        {
            return show(dialog);
        }
        finally
        {
            // Remove only this dialog: a nested Show closing must not forget the outer one.
            _open.Remove(dialog);
        }
    }
}
