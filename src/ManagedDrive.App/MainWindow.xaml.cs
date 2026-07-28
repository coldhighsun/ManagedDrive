using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ManagedDrive.App;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan SpeedPopupCloseDelay = TimeSpan.FromMilliseconds(150);

    private DispatcherTimer? _speedPopupCloseTimer;
    private Popup? _pendingCloseSpeedPopup;

    /// <summary>
    /// Initializes the main window and binds the supplied view model.
    /// </summary>
    /// <param name="viewModel">The view model to bind as <c>DataContext</c>.</param>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        WindowMaximizeHelper.HookMaximizeBehavior(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ImportBtn_Click(object sender, RoutedEventArgs e) => OpenAttachedContextMenu(sender);

    private void OverflowBtn_Click(object sender, RoutedEventArgs e) => OpenAttachedContextMenu(sender);

    private void OpenAttachedContextMenu(object sender)
    {
        if (sender is FrameworkElement { ContextMenu: not null } btn)
        {
            btn.ContextMenu.DataContext = DataContext;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    /// <summary>
    /// Opens the speed-history popup when the mouse enters the speed row or the popup itself,
    /// cancelling any pending close scheduled for a different popup.
    /// </summary>
    private void SpeedRow_MouseEnter(object sender, MouseEventArgs e)
    {
        var popup = FindSpeedPopup(sender);
        if (popup is null)
        {
            return;
        }

        if (_pendingCloseSpeedPopup is not null && _pendingCloseSpeedPopup != popup)
        {
            CloseSpeedPopup(_pendingCloseSpeedPopup);
        }

        _speedPopupCloseTimer?.Stop();
        _pendingCloseSpeedPopup = null;
        popup.IsOpen = true;
    }

    /// <summary>
    /// Schedules the speed-history popup to close shortly after the mouse leaves the speed row
    /// or the popup itself, so briefly crossing the gap between them doesn't flicker it shut.
    /// </summary>
    private void SpeedRow_MouseLeave(object sender, MouseEventArgs e)
    {
        var popup = FindSpeedPopup(sender);
        if (popup is null)
        {
            return;
        }

        _speedPopupCloseTimer?.Stop();
        _pendingCloseSpeedPopup = popup;
        _speedPopupCloseTimer = new DispatcherTimer { Interval = SpeedPopupCloseDelay };
        _speedPopupCloseTimer.Tick += (_, _) =>
        {
            _speedPopupCloseTimer?.Stop();
            CloseSpeedPopup(popup);
            _pendingCloseSpeedPopup = null;
        };
        _speedPopupCloseTimer.Start();
    }

    private static void CloseSpeedPopup(Popup popup) => popup.IsOpen = false;

    /// <summary>
    /// Resolves the speed-history <see cref="Popup"/> for a mouse event raised either by the
    /// speed row itself or by the popup's own content border. The border's <c>Tag</c> is bound to
    /// the popup by name in XAML (<c>Tag="{Binding ElementName=SpeedPopup}"</c>) rather than
    /// resolved here via <c>FrameworkElement.Parent</c> — a <see cref="Popup"/>'s child is hosted
    /// in a separate visual root, so relying on logical-tree parentage to hold at arbitrary
    /// MouseEnter/MouseLeave timing is fragile; the explicit binding is resolved once when the
    /// template loads, before any such event can fire.
    /// </summary>
    private static Popup? FindSpeedPopup(object sender) => sender switch
    {
        StackPanel speedRow => speedRow.Children.OfType<Popup>().FirstOrDefault(),
        FrameworkElement { Tag: Popup popup } => popup,
        _ => null,
    };
}