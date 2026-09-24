using System.Windows.Input;
using System.Windows.Shell;

namespace ManagedDrive.App.Views;

/// <summary>
/// Base class for borderless modal dialogs sharing the same custom title bar (close button
/// bound to <see cref="CloseDialogCommand"/>) and <see cref="WindowChrome"/> setup, defined once
/// in <c>DialogWindowStyle</c> in <c>Themes/AppTheme.xaml</c>. Not used by <c>MainWindow</c>,
/// which has a different, resizable, toolbar-based chrome.
/// </summary>
public class DialogWindowBase : Window
{
    /// <summary>
    /// Command bound to the shared title bar's close button (see <c>DialogWindowStyle</c> in
    /// <c>Themes/AppTheme.xaml</c>). Cannot use a named XAML <c>Click</c> handler instead, since
    /// the shared <c>ControlTemplate</c> lives in a <see cref="ResourceDictionary"/> with no
    /// compiled code-behind scope to resolve one against.
    /// </summary>
    public static readonly RoutedCommand CloseDialogCommand = new();

    /// <summary>
    /// Initializes the borderless window chrome and wires up <see cref="CloseDialogCommand"/>.
    /// </summary>
    protected DialogWindowBase()
    {
        WindowChrome.SetWindowChrome(this, new()
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new(0),
            GlassFrameThickness = new(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
        });

        CommandBindings.Add(new(CloseDialogCommand, CloseButton_Click));
    }

    /// <summary>
    /// Subscribes so this dialog closes itself if <paramref name="target"/> is disposed while the
    /// dialog is open. WPF's modal-disable from <c>ShowDialog()</c> only blocks the owner window —
    /// the tray icon's WinForms context menu stays interactive and can unmount the very disk a
    /// dialog is showing — so this closes the dialog immediately rather than let it keep running
    /// against a disk that's now unmounted/disposed. Also covers the narrow race where disposal
    /// happens before this window's <see cref="FrameworkElement.Loaded"/> has fired (e.g. between
    /// this constructor returning and the caller's <c>ShowDialog()</c> call): the close is deferred
    /// to <see cref="FrameworkElement.Loaded"/> instead of being silently skipped. Automatically
    /// unsubscribes when this dialog closes.
    /// </summary>
    /// <param name="target">The disk whose disposal should close this dialog.</param>
    protected void CloseOnDisposing(DiskViewModel target)
    {
        var targetDisposed = false;

        target.Disposing += OnDisposing;
        Closed += (_, _) => target.Disposing -= OnDisposing;
        Loaded += (_, _) =>
        {
            if (targetDisposed)
            {
                Close();
            }
        };

        void OnDisposing(object? sender, EventArgs e) =>
            Dispatcher.BeginInvoke(() =>
            {
                targetDisposed = true;
                if (IsLoaded)
                {
                    Close();
                }
            });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
