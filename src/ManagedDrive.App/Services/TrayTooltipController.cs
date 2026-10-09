using System.Windows.Controls.Primitives;

namespace ManagedDrive.App.Services;

/// <summary>
/// Owns the hover-tooltip popup shown near the tray icon: cursor tracking, the show/hide timers,
/// and popup positioning. Extracted from <see cref="App"/>; depends on the tray icon only through
/// <see cref="ITrayHoverSource"/>: the cursor position reported via
/// <see cref="ITrayHoverSource.MouseMoved"/> and the dismissal signal from
/// <see cref="ITrayHoverSource.ContextMenuOpening"/>.
/// </summary>
public sealed class TrayTooltipController : IDisposable
{
    private readonly DispatcherTimer _timerPollCursor = new()
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };
    private readonly DispatcherTimer _timerShowTrayInfoPopup = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };
    private readonly DispatcherTimer _timerTooltipCooldown = new()
    {
        Interval = TimeSpan.FromMilliseconds(300)
    };
    private readonly Popup _trayInfoPopup;
    private System.Drawing.Point _iconScreenPoint;

    /// <summary>
    /// The DPI scale in effect when the tooltip was last shown.
    /// </summary>
    private System.Windows.DpiScale _dpi = new(1, 1);

    private bool _tooltipCooldown;

    /// <summary>
    /// The data context given to each new tooltip view.
    /// </summary>
    private readonly object _dataContext;

    /// <summary>
    /// Refreshes the data context before each show.
    /// </summary>
    private readonly Action _refresh;

    /// <summary>
    /// The source whose events this controller subscribed to, kept so <see cref="Dispose"/> can
    /// unsubscribe.
    /// </summary>
    private readonly ITrayHoverSource _hoverSource;

    /// <summary>
    /// Builds the tooltip popup and wires it to <paramref name="trayIconController"/>'s cursor
    /// tracking.
    /// </summary>
    /// <param name="mainViewModel">Data context for <see cref="TrayTooltipView"/>, refreshed just before each show.</param>
    /// <param name="trayIconController">Supplies the tray icon's screen position via <see cref="TrayIconController.MouseMoved"/>.</param>
    public TrayTooltipController(MainViewModel mainViewModel, TrayIconController trayIconController)
        : this(mainViewModel, mainViewModel.RefreshForTrayTooltip, trayIconController)
    {
    }

    /// <summary>
    /// Builds the tooltip popup around any data context and hover source; the seam used by tests.
    /// </summary>
    /// <param name="dataContext">Data context for <see cref="TrayTooltipView"/>.</param>
    /// <param name="refresh">Refreshes <paramref name="dataContext"/> just before each show and when the language changes while shown.</param>
    /// <param name="hoverSource">Supplies the tray icon's screen position and context-menu notifications.</param>
    internal TrayTooltipController(object dataContext, Action refresh, ITrayHoverSource hoverSource)
    {
        _dataContext = dataContext;
        _refresh = refresh;
        _hoverSource = hoverSource;
        _trayInfoPopup = new()
        {
            Placement = PlacementMode.AbsolutePoint,
            AllowsTransparency = true,
            StaysOpen = true,
        };

        hoverSource.MouseMoved += OnMouseMoved;
        hoverSource.ContextMenuOpening += HideTooltip;

        _timerShowTrayInfoPopup.Tick += (_, _) => ShowTooltip();

        _timerPollCursor.Tick += (_, _) =>
        {
            if (_trayInfoPopup is not { IsOpen: true })
            {
                _timerPollCursor.Stop();
                return;
            }

            var cur = System.Windows.Forms.Cursor.Position;
            if (IsInIconRegion(cur) || IsInPopupRegion(cur))
            {
                return;
            }

            HideTooltip();
        };

        _timerTooltipCooldown.Tick += (_, _) =>
        {
            _tooltipCooldown = false;
            _timerTooltipCooldown.Stop();
        };

        LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Unsubscribes from the hover source and the static language event, stops all timers and
    /// closes the tooltip.
    /// </summary>
    public void Dispose()
    {
        _hoverSource.MouseMoved -= OnMouseMoved;
        _hoverSource.ContextMenuOpening -= HideTooltip;
        LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;
        _timerShowTrayInfoPopup.Stop();
        _timerPollCursor.Stop();
        _timerTooltipCooldown.Stop();
        ClosePopup(_trayInfoPopup);
    }

    /// <summary>
    /// Gets a value indicating whether the tooltip is currently open.
    /// </summary>
    internal bool IsOpen => _trayInfoPopup.IsOpen;

    /// <summary>
    /// Gets the tooltip's current content, or <c>null</c> while nothing is shown.
    /// </summary>
    internal UIElement? Content => _trayInfoPopup.Child;

    /// <summary>
    /// Gets a value indicating whether a show is scheduled after the hover delay.
    /// </summary>
    internal bool IsShowPending => _timerShowTrayInfoPopup.IsEnabled;

    /// <summary>
    /// Shows the tooltip now with a freshly built view, positioned next to the tray icon.
    /// </summary>
    internal void ShowTooltip()
    {
        _timerShowTrayInfoPopup.Stop();
        _refresh();
        ReplaceContent(_trayInfoPopup, () => new TrayTooltipView { DataContext = _dataContext });
        _dpi = ResolveDpi(_trayInfoPopup.Child);
        PositionTrayPopup();
        _trayInfoPopup.IsOpen = true;
        _timerPollCursor.Start();
    }

    /// <summary>
    /// Cancels a pending show, closes the tooltip and drops its content, then suppresses new
    /// shows for a short cooldown so it does not flicker straight back.
    /// </summary>
    internal void HideTooltip()
    {
        _timerShowTrayInfoPopup.Stop();
        _timerPollCursor.Stop();
        ClosePopup(_trayInfoPopup);
        _tooltipCooldown = true;
        _timerTooltipCooldown.Start();
    }

    /// <summary>
    /// Gives <paramref name="popup"/> a freshly created content element. The popup lives in its
    /// own visual tree, so a view kept across shows would not re-resolve its DynamicResource
    /// brushes after the theme palette is swapped; a new view always resolves against the
    /// current resources. The content is measured right away so its desired size is valid before
    /// the popup has laid it out (see <see cref="GetContentSize"/>).
    /// </summary>
    /// <param name="popup">The popup whose content is replaced.</param>
    /// <param name="createContent">Creates the new content element.</param>
    internal static void ReplaceContent(Popup popup, Func<UIElement> createContent)
    {
        var content = createContent();
        content.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        popup.Child = content;
    }

    /// <summary>
    /// Closes <paramref name="popup"/> and drops its content so the last tooltip view does not
    /// stay bound to the view model while nothing is shown.
    /// </summary>
    /// <param name="popup">The popup to close.</param>
    internal static void ClosePopup(Popup popup)
    {
        popup.IsOpen = false;
        popup.Child = null;
    }

    /// <summary>
    /// Gets the size of <paramref name="content"/>, falling back to its measured desired size
    /// while the popup has not laid it out yet and <see cref="FrameworkElement.ActualWidth"/> and
    /// <see cref="FrameworkElement.ActualHeight"/> are still zero.
    /// </summary>
    /// <param name="content">The popup content.</param>
    /// <returns>The laid-out size, or the desired size before the first layout pass.</returns>
    internal static System.Windows.Size GetContentSize(FrameworkElement content) =>
        content.ActualWidth > 0 && content.ActualHeight > 0
            ? new(content.ActualWidth, content.ActualHeight)
            : content.DesiredSize;

    /// <summary>
    /// Gets the DPI scale used to convert between physical pixels and device-independent units,
    /// taken from the main window when there is one and otherwise from <paramref name="fallback"/>,
    /// which avoids creating a throwaway window just to read the DPI.
    /// </summary>
    /// <param name="fallback">An element to take the DPI from when there is no main window.</param>
    /// <returns>The DPI scale.</returns>
    internal static System.Windows.DpiScale ResolveDpi(UIElement? fallback)
    {
        var source = Application.Current?.MainWindow ?? fallback;
        return source is null ? new(1, 1) : System.Windows.Media.VisualTreeHelper.GetDpi(source);
    }

    /// <summary>
    /// Computes the top-left corner of the tooltip: horizontally centered on the icon and kept
    /// inside <paramref name="workArea"/>, above the icon, or pinned to whichever work-area edge
    /// the icon is next to.
    /// </summary>
    /// <param name="workArea">The primary monitor's work area in device-independent units.</param>
    /// <param name="icon">The icon position in device-independent units.</param>
    /// <param name="popup">The tooltip size in device-independent units.</param>
    /// <returns>The tooltip's top-left corner in device-independent units.</returns>
    internal static System.Windows.Point ComputePopupOrigin(Rect workArea, System.Windows.Point icon, System.Windows.Size popup)
    {
        var left = icon.X - popup.Width / 2;
        if (left < workArea.Left)
        {
            left = workArea.Left + 4;
        }

        if (left + popup.Width > workArea.Right)
        {
            left = workArea.Right - popup.Width - 4;
        }

        double top;
        if (icon.Y > workArea.Bottom - 60)
        {
            top = workArea.Bottom - popup.Height - 8;
        }
        else if (icon.Y < workArea.Top + 60)
        {
            top = workArea.Top + 8;
        }
        else
        {
            top = icon.Y - popup.Height - 16;
        }

        return new(left, top);
    }

    /// <summary>
    /// Remembers the icon position and schedules a show after the hover delay unless the tooltip
    /// is open or cooling down.
    /// </summary>
    /// <param name="point">The icon position in physical screen pixels.</param>
    private void OnMouseMoved(System.Drawing.Point point)
    {
        _iconScreenPoint = point;
        if (!_trayInfoPopup.IsOpen && !_tooltipCooldown)
        {
            _timerShowTrayInfoPopup.Start();
        }
    }

    /// <summary>
    /// DynamicResource bindings inside the popup's content don't reliably re-resolve while
    /// IsOpen is false, so a tooltip already open when the language changes needs a nudge.
    /// </summary>
    /// <param name="sender">The language manager.</param>
    /// <param name="e">Unused event data.</param>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_trayInfoPopup.IsOpen)
        {
            _refresh();
        }
    }

    /// <summary>
    /// Determines whether <paramref name="cursor"/> is close enough to the tray icon to keep the
    /// tooltip open.
    /// </summary>
    /// <param name="cursor">The cursor position in physical screen pixels.</param>
    /// <returns><c>true</c> if the cursor is within the icon's hover region.</returns>
    private bool IsInIconRegion(System.Drawing.Point cursor)
    {
        const int halfSize = 16;
        return Math.Abs(cursor.X - _iconScreenPoint.X) <= halfSize
            && Math.Abs(cursor.Y - _iconScreenPoint.Y) <= halfSize;
    }

    /// <summary>
    /// Determines whether <paramref name="cursor"/> is over the tooltip or its surrounding margin,
    /// using the DPI scale captured when the tooltip was shown.
    /// </summary>
    /// <param name="cursor">The cursor position in physical screen pixels.</param>
    /// <returns><c>true</c> if the cursor is within the tooltip's hover region.</returns>
    private bool IsInPopupRegion(System.Drawing.Point cursor)
    {
        if (_trayInfoPopup.Child is not FrameworkElement child)
        {
            return false;
        }

        var margin = 16.0;
        var left = _trayInfoPopup.HorizontalOffset * _dpi.DpiScaleX - margin;
        var top = _trayInfoPopup.VerticalOffset * _dpi.DpiScaleY - margin;
        var size = GetContentSize(child);
        var right = left + size.Width * _dpi.DpiScaleX + margin * 2;
        var bottom = top + size.Height * _dpi.DpiScaleY + margin * 2;
        return cursor.X >= left && cursor.X <= right && cursor.Y >= top && cursor.Y <= bottom;
    }

    /// <summary>
    /// Places the tooltip next to the tray icon inside the primary monitor's work area. Expects
    /// the popup content to be set and measured already (see <see cref="ReplaceContent"/>).
    /// </summary>
    private void PositionTrayPopup()
    {
        if (_trayInfoPopup.Child is not FrameworkElement child)
        {
            return;
        }

        var popupSize = child.DesiredSize;
        var icon = new System.Windows.Point(_iconScreenPoint.X / _dpi.DpiScaleX, _iconScreenPoint.Y / _dpi.DpiScaleY);

        var origin = ComputePopupOrigin(SystemParameters.WorkArea, icon, popupSize);
        _trayInfoPopup.HorizontalOffset = origin.X;
        _trayInfoPopup.VerticalOffset = origin.Y;
    }
}
