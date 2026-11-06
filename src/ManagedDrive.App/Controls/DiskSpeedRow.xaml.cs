using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ManagedDrive.App.Controls;

/// <summary>
/// The read/write throughput row of a disk card. Hovering it opens a popup with the speed history after a
/// short delay and closes it again shortly after the mouse leaves.
/// </summary>
public partial class DiskSpeedRow
{
    private static readonly TimeSpan PopupOpenDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PopupCloseDelay = TimeSpan.FromMilliseconds(150);

    // Shared by all rows: only one popup is pending or open at a time.
    private static DispatcherTimer? _openTimer;
    private static DispatcherTimer? _closeTimer;
    private static Popup? _pendingClosePopup;

    /// <summary>
    /// Initializes the row and wires the hover handlers of the row and its popup.
    /// </summary>
    public DiskSpeedRow()
    {
        InitializeComponent();
        SpeedHoverArea.MouseEnter += OnMouseEnter;
        SpeedHoverArea.MouseLeave += OnMouseLeave;
        PopupBorder.MouseEnter += OnMouseEnter;
        PopupBorder.MouseLeave += OnMouseLeave;
    }

    /// <summary>
    /// Schedules the speed-history popup to open after <see cref="PopupOpenDelay"/> once the
    /// mouse enters the speed row or the popup itself, cancelling any pending close scheduled for
    /// a different popup. Already-open popups stay open immediately, without re-running the delay.
    /// </summary>
    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        var popup = FindSpeedPopup(sender);
        if (popup is null)
        {
            return;
        }

        if (_pendingClosePopup is not null && _pendingClosePopup != popup)
        {
            CloseSpeedPopup(_pendingClosePopup);
        }

        _closeTimer?.Stop();
        _pendingClosePopup = null;

        if (popup.IsOpen)
        {
            return;
        }

        _openTimer?.Stop();
        _openTimer = new() { Interval = PopupOpenDelay };
        _openTimer.Tick += (_, _) =>
        {
            _openTimer?.Stop();
            popup.IsOpen = true;
        };
        _openTimer.Start();
    }

    /// <summary>
    /// Cancels a pending open, and schedules the speed-history popup to close shortly after the
    /// mouse leaves the speed row or the popup itself, so briefly crossing the gap between them
    /// doesn't flicker it shut.
    /// </summary>
    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        var popup = FindSpeedPopup(sender);
        if (popup is null)
        {
            return;
        }

        _openTimer?.Stop();

        if (!popup.IsOpen)
        {
            return;
        }

        _closeTimer?.Stop();
        _pendingClosePopup = popup;
        _closeTimer = new() { Interval = PopupCloseDelay };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer?.Stop();
            CloseSpeedPopup(popup);
            _pendingClosePopup = null;
        };
        _closeTimer.Start();
    }

    private static void CloseSpeedPopup(Popup popup) => popup.IsOpen = false;

    /// <summary>
    /// Resolves the speed-history <see cref="Popup"/> for a mouse event raised either by the
    /// speed row itself or by the popup's own content border. The border's <c>Tag</c> is bound to
    /// the popup by name in XAML (<c>Tag="{Binding ElementName=SpeedPopup}"</c>) rather than
    /// resolved here via <c>FrameworkElement.Parent</c> — a <see cref="Popup"/>'s child is hosted
    /// in a separate visual root, so relying on logical-tree parentage to hold at arbitrary
    /// MouseEnter/MouseLeave timing is fragile; the explicit binding is resolved once when the
    /// control loads, before any such event can fire.
    /// </summary>
    private static Popup? FindSpeedPopup(object sender) => sender switch
    {
        FrameworkElement { Tag: Popup popup } => popup,
        _ => null,
    };
}
