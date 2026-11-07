namespace ManagedDrive.App.Views;

/// <summary>
/// The app's replacement for <see cref="MessageBox"/>: the same call shape, but shown as a themed
/// <see cref="ConfirmDialog"/> instead of the native box.
/// </summary>
public static class AppMessageBox
{
    private static readonly ILogger Logger = AppLog.CreateLogger(typeof(AppMessageBox));

    /// <summary>
    /// Shows a modal message. Falls back to the native <see cref="MessageBox"/> when the themed dialog
    /// cannot be shown (no application or window yet, resources not loaded, or the dialog failed to build), so a
    /// startup or crash message is never lost.
    /// </summary>
    /// <param name="message">The text of the message.</param>
    /// <param name="title">The title shown next to the icon.</param>
    /// <param name="button">any <see cref="MessageBoxButton"/> value.</param>
    /// <param name="image">The icon; decides the <see cref="ConfirmKind"/>.</param>
    /// <returns>The button pressed; Esc and the close box give No for YesNo and Cancel for OKCancel and YesNoCancel.</returns>
    public static MessageBoxResult Show(
        string? message,
        string title,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None)
    {
        var app = Application.Current;
        if (app is null)
        {
            return MessageBox.Show(message ?? string.Empty, title, button, image);
        }

        // WPF windows belong to the UI thread; the native box worked from any thread. Every current caller
        // is already on it (command handlers after an await), so this hop is only a safety net.
        if (!app.Dispatcher.CheckAccess())
        {
            return app.Dispatcher.Invoke(() => Show(message, title, button, image));
        }

        // With no window yet (startup prompts before the splash), the dialog would become
        // Application.MainWindow and closing it would end the app under OnLastWindowClose.
        if (app.Windows.Count == 0)
        {
            return MessageBox.Show(message ?? string.Empty, title, button, image);
        }

        var yes = button is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel;
        var hasCancel = button != MessageBoxButton.OK;
        ConfirmDialog dialog;
        try
        {
            dialog = new ConfirmDialog(title, message ?? string.Empty)
            {
                Kind = ToKind(image),
                ShowCancel = hasCancel,
            };

            switch (button)
            {
                case MessageBoxButton.YesNo:
                    dialog.OkText = Loc.Get("Btn.Yes");
                    dialog.CancelText = Loc.Get("Btn.No");
                    break;
                case MessageBoxButton.YesNoCancel:
                    dialog.OkText = Loc.Get("Btn.Yes");
                    dialog.NeutralText = Loc.Get("Btn.No");
                    break;
            }

            var owner = FindOwner();
            if (owner is not null)
            {
                dialog.Owner = owner;
            }
            else
            {
                // CenterOwner without an owner would leave the dialog at the default top-left position
                // (e.g. over the splash or with the main window hidden in the tray).
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.LogWarning(ex, "Themed message box failed; using the native one");
            return MessageBox.Show(message ?? string.Empty, title, button, image);
        }

        // Once the dialog was on screen the user may have answered, so a failure must not ask again;
        // only a dialog that never got shown falls back to the native box.
        var shown = false;
        dialog.Loaded += (_, _) => shown = true;
        bool accepted;
        try
        {
            accepted = dialog.ShowDialog() == true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Already answered or not is unknown once shown, so answer with the non-affirmative result
            // (No / Cancel), the safe one for a confirmation, and make the failure visible in the log.
            Logger.LogError(ex, "Themed message box failed while showing");
            if (!shown)
            {
                return MessageBox.Show(message ?? string.Empty, title, button, image);
            }

            accepted = false;
        }

        if (accepted)
        {
            return yes ? MessageBoxResult.Yes : MessageBoxResult.OK;
        }

        return button switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel when dialog.IsNeutralChosen => MessageBoxResult.No,
            MessageBoxButton.OK => MessageBoxResult.OK,
            _ => MessageBoxResult.Cancel,
        };
    }

    private static ConfirmKind ToKind(MessageBoxImage image) => image switch
    {
        MessageBoxImage.Error => ConfirmKind.Error,
        MessageBoxImage.Question => ConfirmKind.Question,
        MessageBoxImage.Information => ConfirmKind.Information,
        MessageBoxImage.None => ConfirmKind.None,
        _ => ConfirmKind.Warning,
    };

    /// <summary>
    /// Picks the window that should own the dialog: only the main window or another dialog. The splash and the
    /// tray hover popup are transient, and closing an owner closes the dialogs it owns, which would answer the
    /// message for the user.
    /// </summary>
    private static Window? FindOwner()
    {
        var main = Application.Current.MainWindow;
        Window? lastVisible = null;
        foreach (Window window in Application.Current.Windows)
        {
            if (!window.IsVisible || !(window == main || window is DialogWindowBase))
            {
                continue;
            }

            if (window.IsActive)
            {
                return window;
            }

            lastVisible = window;
        }

        // App not in the foreground (tray, CLI hand-off): still own the dialog so it is centered and
        // stays above the owner instead of opening ownerless behind another window.
        return main is { IsVisible: true } ? main : lastVisible;
    }
}
