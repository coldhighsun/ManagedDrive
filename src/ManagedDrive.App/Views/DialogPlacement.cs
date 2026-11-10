using System.Runtime.CompilerServices;

namespace ManagedDrive.App.Views;

/// <summary>
/// Placement helpers for dialogs that can be shown while the main window is hidden.
/// </summary>
internal static class DialogPlacement
{
    /// <summary>
    /// The dialogs <see cref="BringToFrontWhenUnowned"/> has already handled.
    /// </summary>
    private static readonly ConditionalWeakTable<Window, object?> Handled = [];

    /// <summary>
    /// Makes an ownerless dialog reachable. The dialogs are created without a taskbar button and
    /// normally follow their owner; with the main window hidden (started minimized, or closed to
    /// the tray) there is no owner, so without this the dialog can open behind other windows with
    /// no way to find it, and whatever awaits its result waits forever.
    /// </summary>
    /// <param name="dialog">The dialog that was just loaded.</param>
    public static void BringToFrontWhenUnowned(Window dialog)
    {
        // Loaded can fire again if the dialog is re-attached; raise and release only once.
        if (dialog.Owner is not null || !Handled.TryAdd(dialog, null))
        {
            return;
        }

        // Topmost lifts the dialog above the windows that cover it. It is dropped once the dialog
        // is active, so a long-lived dialog (settings) does not stay pinned over every other
        // application, yet is not sent behind the windows it was just raised over. Windows may
        // refuse the activation (foreground lock, another app has focus), in which case Activated
        // never fires; the timer then drops it anyway, and the taskbar button flashes instead.
        var topmost = new TransientTopmost(value => dialog.Topmost = value);
        var fallback = new DispatcherTimer { Interval = TopmostFallback };

        void Release()
        {
            fallback.Stop();
            dialog.Activated -= OnActivated;
            topmost.Release();
        }

        void OnActivated(object? sender, EventArgs e) => Release();

        fallback.Tick += (_, _) => Release();
        dialog.Closed += (_, _) => Release();
        dialog.Activated += OnActivated;

        dialog.ShowInTaskbar = true;
        topmost.Raise();
        fallback.Start();
        dialog.Activate();
        if (dialog.IsActive)
        {
            Release();
        }
    }

    /// <summary>
    /// How long an unowned dialog may stay topmost waiting for activation before it is released
    /// regardless.
    /// </summary>
    private static readonly TimeSpan TopmostFallback = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Brings an already open dialog to the front, restoring it first if it was minimized (an
    /// unowned dialog has a taskbar button, and <see cref="Window.Activate"/> alone would leave it
    /// there).
    /// </summary>
    /// <param name="dialog">The open dialog.</param>
    public static void BringForward(Window dialog)
    {
        if (dialog.WindowState == WindowState.Minimized)
        {
            dialog.WindowState = WindowState.Normal;
        }

        dialog.Activate();
    }
}
