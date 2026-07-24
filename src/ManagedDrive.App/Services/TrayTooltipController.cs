using System.Windows.Controls.Primitives;

namespace ManagedDrive.App.Services;

/// <summary>
/// Owns the hover-tooltip popup shown near the tray icon: cursor tracking, the show/hide timers,
/// and popup positioning. Extracted from <see cref="App"/>; depends on <see cref="TrayIconController"/>
/// only for the cursor position reported via <see cref="TrayIconController.MouseMoved"/>.
/// </summary>
public sealed class TrayTooltipController
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
    private bool _tooltipCooldown;

    /// <summary>
    /// Builds the tooltip popup and wires it to <paramref name="trayIconController"/>'s cursor
    /// tracking.
    /// </summary>
    /// <param name="mainViewModel">Data context for <see cref="TrayTooltipView"/>, refreshed just before each show.</param>
    /// <param name="trayIconController">Supplies the tray icon's screen position via <see cref="TrayIconController.MouseMoved"/>.</param>
    public TrayTooltipController(MainViewModel mainViewModel, TrayIconController trayIconController)
    {
        _trayInfoPopup = new()
        {
            Child = new TrayTooltipView { DataContext = mainViewModel },
            Placement = PlacementMode.AbsolutePoint,
            AllowsTransparency = true,
            StaysOpen = true,
        };

        trayIconController.MouseMoved += point =>
        {
            _iconScreenPoint = point;
            if (!_trayInfoPopup.IsOpen && !_tooltipCooldown)
            {
                _timerShowTrayInfoPopup.Start();
            }
        };

        _timerShowTrayInfoPopup.Tick += (_, _) =>
        {
            _timerShowTrayInfoPopup.Stop();
            mainViewModel.RefreshForTrayTooltip();
            PositionTrayPopup();
            _trayInfoPopup.IsOpen = true;
            _timerPollCursor.Start();
        };

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

            _trayInfoPopup.IsOpen = false;
            _timerPollCursor.Stop();
            _tooltipCooldown = true;
            _timerTooltipCooldown.Start();
        };

        _timerTooltipCooldown.Tick += (_, _) =>
        {
            _tooltipCooldown = false;
            _timerTooltipCooldown.Stop();
        };
    }

    private bool IsInIconRegion(System.Drawing.Point cursor)
    {
        const int halfSize = 16;
        return Math.Abs(cursor.X - _iconScreenPoint.X) <= halfSize
            && Math.Abs(cursor.Y - _iconScreenPoint.Y) <= halfSize;
    }

    private bool IsInPopupRegion(System.Drawing.Point cursor)
    {
        if (_trayInfoPopup.Child is not FrameworkElement child)
        {
            return false;
        }

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(System.Windows.Application.Current.MainWindow ?? new Window());
        var margin = 16.0;
        var left = _trayInfoPopup.HorizontalOffset * dpi.DpiScaleX - margin;
        var top = _trayInfoPopup.VerticalOffset * dpi.DpiScaleY - margin;
        var right = left + child.ActualWidth * dpi.DpiScaleX + margin * 2;
        var bottom = top + child.ActualHeight * dpi.DpiScaleY + margin * 2;
        return cursor.X >= left && cursor.X <= right && cursor.Y >= top && cursor.Y <= bottom;
    }

    private void PositionTrayPopup()
    {
        var workArea = SystemParameters.WorkArea;
        var child = _trayInfoPopup.Child as FrameworkElement;
        child?.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        var popupHeight = child?.DesiredSize.Height ?? 80;
        var popupWidth = child?.DesiredSize.Width ?? 200;

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(System.Windows.Application.Current.MainWindow ?? new Window());
        var x = _iconScreenPoint.X / dpi.DpiScaleX;
        var y = _iconScreenPoint.Y / dpi.DpiScaleY;

        var left = x - popupWidth / 2;
        if (left < workArea.Left)
            left = workArea.Left + 4;
        if (left + popupWidth > workArea.Right)
            left = workArea.Right - popupWidth - 4;

        double top;
        if (y > workArea.Bottom - 60)
            top = workArea.Bottom - popupHeight - 8;
        else if (y < workArea.Top + 60)
            top = workArea.Top + 8;
        else
            top = y - popupHeight - 16;

        _trayInfoPopup.HorizontalOffset = left;
        _trayInfoPopup.VerticalOffset = top;
    }
}