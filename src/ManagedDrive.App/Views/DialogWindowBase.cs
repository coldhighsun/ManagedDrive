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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}