using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ManagedDrive.App.Services;

/// <summary>
/// Owns the system tray icon: the base/read/write icon variants, the context menu and its
/// theme/language wiring, and the activity-flash indicator. Extracted from <see cref="App"/> so
/// tray-icon concerns live in one place, separate from tooltip popup handling
/// (<see cref="TrayTooltipController"/>) and application lifecycle.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    /// <summary>
    /// How long the tray icon shows its read/write indicator after <see cref="OnActivityDetected"/>
    /// before reverting to the idle icon.
    /// </summary>
    private static readonly TimeSpan ActivityFlashDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Toggle interval for the high-usage warning blink, driven by <see cref="SetHighUsageWarningActive"/>.
    /// Deliberately slower than <see cref="ActivityFlashDuration"/> so the two remain visually distinct.
    /// </summary>
    private static readonly TimeSpan HighUsageBlinkInterval = TimeSpan.FromMilliseconds(800);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timerActivityFlash = new()
    {
        Interval = ActivityFlashDuration
    };
    private readonly DispatcherTimer _timerHighUsageBlink = new()
    {
        Interval = HighUsageBlinkInterval
    };
    private readonly IntPtr[] _trayActivityIconHandles = new IntPtr[4];

    /// <summary>
    /// [0] normal, [1] read indicator, [2] write indicator, [3] high-usage warning indicator.
    /// Generated once at startup from the base tray icon.
    /// </summary>
    private readonly Icon?[] _trayActivityIcons = new Icon?[4];

    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly Icon _trayIconNormal;
    private bool _blinkOn;
    private bool _isHighUsageActive;

    /// <summary>
    /// Builds the tray icon, its context menu, and the activity-flash timer.
    /// </summary>
    /// <param name="dispatcher">The UI dispatcher; menu/mouse callbacks are marshalled through it.</param>
    /// <param name="iconStream">Stream containing the base <c>.ico</c> resource.</param>
    /// <param name="onShow">Invoked from the "Show" menu item and double-click.</param>
    /// <param name="onNewDisk">Invoked from the "New Disk" menu item.</param>
    /// <param name="onResetTempDirsAsync">Invoked from the "Reset TEMP Dirs" menu item.</param>
    /// <param name="onSettings">Invoked from the "Settings" menu item.</param>
    /// <param name="onAbout">Invoked from the "About" menu item.</param>
    /// <param name="onExit">Invoked from the "Exit" menu item.</param>
    public TrayIconController(
        Dispatcher dispatcher,
        Stream iconStream,
        Action onShow,
        Action onNewDisk,
        Func<Task> onResetTempDirsAsync,
        Action onSettings,
        Action onAbout,
        Action onExit)
    {
        _dispatcher = dispatcher;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.Get("Tray.Show"), null, (_, _) => dispatcher.Invoke(onShow));
        menu.Items.Add(Loc.Get("Tray.NewDisk"), null, (_, _) => dispatcher.Invoke(onNewDisk));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.Get("Tray.ResetTempDirs"), null, async (_, _) => await dispatcher.InvokeAsync(onResetTempDirsAsync));
        menu.Items.Add(Loc.Get("Tray.Settings"), null, (_, _) => dispatcher.Invoke(onSettings));
        menu.Items.Add(Loc.Get("Tray.About"), null, (_, _) => dispatcher.Invoke(onAbout));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.Get("Tray.Exit"), null, (_, _) => dispatcher.Invoke(onExit));

        _trayIconNormal = new(iconStream);
        BuildTrayActivityIcons(_trayIconNormal);

        _trayIcon = new()
        {
            Icon = _trayActivityIcons[0],
            ContextMenuStrip = menu,
            Text = "",
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => dispatcher.Invoke(onShow);
        _trayIcon.MouseMove += (_, _) =>
        {
            var point = System.Windows.Forms.Cursor.Position;
            dispatcher.Invoke(() => MouseMoved?.Invoke(point));
        };

        _timerActivityFlash.Tick += (_, _) =>
        {
            SetTrayIcon(_isHighUsageActive && _blinkOn ? _trayActivityIcons[3] : _trayActivityIcons[0]);
            _timerActivityFlash.Stop();
        };

        _timerHighUsageBlink.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;

            // Let an in-progress read/write flash keep showing; its own Tick will pick up the
            // right idle/warning icon (see above) once it reverts.
            if (!_timerActivityFlash.IsEnabled)
            {
                SetTrayIcon(_trayActivityIcons[_blinkOn ? 3 : 0]);
            }
        };

        LanguageManager.Instance.LanguageChanged += (_, _) => UpdateTrayMenuHeaders();
        ApplyTrayMenuTheme();
        ThemeManager.Instance.ThemeChanged += (_, _) => dispatcher.Invoke(ApplyTrayMenuTheme);
    }

    /// <summary>
    /// Raised whenever the cursor moves over the tray icon, carrying its current screen position.
    /// Consumed by <see cref="TrayTooltipController"/> to drive the hover popup.
    /// </summary>
    public event Action<System.Drawing.Point>? MouseMoved;

    /// <summary>
    /// Gets or sets whether the tray icon is visible.
    /// </summary>
    public bool Visible
    {
        get => _trayIcon.Visible;
        set => _trayIcon.Visible = value;
    }

    /// <summary>
    /// Stops the activity and high-usage blink timers and releases the tray icon and all four
    /// generated activity-indicator variants, including the HICONs backing
    /// <see cref="_trayActivityIcons"/> which <see cref="Icon.Dispose"/> alone would leak.
    /// </summary>
    public void Dispose()
    {
        _timerActivityFlash.Stop();
        _timerHighUsageBlink.Stop();
        _trayIcon.Dispose();
        _trayIconNormal.Dispose();

        for (var i = 0; i < _trayActivityIcons.Length; i++)
        {
            _trayActivityIcons[i]?.Dispose();
            _trayActivityIcons[i] = null;

            if (_trayActivityIconHandles[i] != IntPtr.Zero)
            {
                DestroyIcon(_trayActivityIconHandles[i]);
                _trayActivityIconHandles[i] = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Handler for <see cref="MountManager.ActivityDetected"/>. May run on any WinFsp driver
    /// thread, so the actual icon update is dispatched to the UI thread. Flashes the read/write
    /// indicator icon once for <see cref="ActivityFlashDuration"/>, then reverts to idle; the
    /// write indicator takes priority and isn't overridden by a read arriving mid-flash.
    /// </summary>
    public void OnActivityDetected(bool isWrite)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_trayActivityIcons[0] == null)
            {
                return;
            }

            if (isWrite || _trayIcon.Icon != _trayActivityIcons[2])
            {
                SetTrayIcon(_trayActivityIcons[isWrite ? 2 : 1]);
            }

            _timerActivityFlash.Stop();
            _timerActivityFlash.Start();
        });
    }

    /// <summary>
    /// Starts or stops the sustained high-usage warning blink: while active, the tray icon
    /// repeatedly toggles between the idle icon and the warning indicator every
    /// <see cref="HighUsageBlinkInterval"/> until turned off again. Unlike
    /// <see cref="OnActivityDetected"/>'s one-shot flash, this stays on for as long as the
    /// caller reports the condition is active (see <see cref="Services.DiskNotificationService"/>,
    /// which aggregates every disk's <c>IsHighUsage</c> state into a single call here).
    /// </summary>
    /// <param name="active">Whether any disk currently exceeds its high-usage threshold.</param>
    public void SetHighUsageWarningActive(bool active)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (active == _isHighUsageActive)
            {
                return;
            }

            // Record the state change before checking icon readiness below, so a call that
            // arrives before icon generation finishes doesn't get treated as a no-op — otherwise
            // a later, genuine call with the same target value would be dropped by the
            // short-circuit above, permanently losing the warning blink.
            _isHighUsageActive = active;

            if (_trayActivityIcons[3] == null)
            {
                return;
            }

            if (active)
            {
                _blinkOn = true;
                SetTrayIcon(_trayActivityIcons[3]);
                _timerHighUsageBlink.Start();
            }
            else
            {
                _timerHighUsageBlink.Stop();

                if (!_timerActivityFlash.IsEnabled)
                {
                    SetTrayIcon(_trayActivityIcons[0]);
                }
            }
        });
    }

    /// <summary>
    /// Shows a balloon tip from the tray icon.
    /// </summary>
    public void ShowBalloonTip(string title, string body, System.Windows.Forms.ToolTipIcon icon, int timeout = 5000) =>
        _trayIcon.ShowBalloonTip(timeout, title, body, icon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private void ApplyTrayMenuTheme()
    {
        if (_trayIcon.ContextMenuStrip is not { } menu)
        {
            return;
        }

        var isDark = ThemeManager.Instance.CurrentTheme == "dark";
        var background = isDark
            ? Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A)
            : Color.White;
        var foreground = isDark ? Color.White : Color.Black;

        menu.ShowImageMargin = false;
        menu.Renderer = new System.Windows.Forms.ToolStripProfessionalRenderer(new TrayColorTable(isDark));
        menu.BackColor = background;
        menu.ForeColor = foreground;
        foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = foreground;
        }
    }

    /// <summary>
    /// Generates the tray icon variants indexed by <see cref="_trayActivityIcons"/> (0 = normal,
    /// 1 = read indicator, 2 = write indicator, 3 = high-usage warning indicator) by overlaying an
    /// indicator in the top-right corner of <paramref name="baseIcon"/>: a green dot for reads, an
    /// orange dot for writes, and a red exclamation-mark badge (distinct shape, not just a
    /// differently-colored dot) for the warning state — the shape difference keeps the warning
    /// blink from being confused with a write flash at 16x16 tray size. Each generated
    /// <see cref="Icon"/>'s backing HICON is recorded in <see cref="_trayActivityIconHandles"/> so
    /// it can be released via <see cref="DestroyIcon"/> on shutdown, since <see cref="Icon.Dispose"/>
    /// alone does not release a handle obtained from <see cref="Bitmap.GetHicon"/>.
    /// </summary>
    private void BuildTrayActivityIcons(Icon baseIcon)
    {
        var size = baseIcon.Size;
        var dotDiameter = Math.Max(4, size.Width / 3);
        var dotRect = new Rectangle(size.Width - dotDiameter, 0, dotDiameter, dotDiameter);
        var badgeDiameter = Math.Max(dotDiameter, size.Height / 2);
        var badgeRect = new Rectangle(size.Width - badgeDiameter, 0, badgeDiameter, badgeDiameter);
        Brush?[] overlayBrushes =
        [
            null,
            new SolidBrush(Color.FromArgb(255, 0, 230, 118)),
            new SolidBrush(Color.FromArgb(255, 255, 50, 0)),
            null,
        ];

        for (var state = 0; state < _trayActivityIcons.Length; state++)
        {
            using var bitmap = new Bitmap(size.Width, size.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.DrawIcon(baseIcon, new Rectangle(0, 0, size.Width, size.Height));
                if (state == 3)
                {
                    DrawWarningBadge(graphics, badgeRect);
                }
                else if (overlayBrushes[state] is { } brush)
                {
                    graphics.FillEllipse(brush, dotRect);
                }
            }

            var hicon = bitmap.GetHicon();
            _trayActivityIconHandles[state] = hicon;
            _trayActivityIcons[state] = Icon.FromHandle(hicon);
        }
    }

    /// <summary>
    /// Draws a red exclamation-mark warning badge (filled triangle with a white "!" glyph built
    /// from two primitives rather than rendered text, so it stays legible at tray-icon scale
    /// without depending on font hinting/DPI) inside <paramref name="bounds"/>.
    /// </summary>
    private static void DrawWarningBadge(Graphics graphics, Rectangle bounds)
    {
        using var fillBrush = new SolidBrush(Color.FromArgb(255, 230, 0, 0));
        using var outlinePen = new Pen(Color.FromArgb(255, 90, 0, 0), Math.Max(1f, bounds.Width / 8f));
        using var glyphBrush = new SolidBrush(Color.White);

        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        System.Drawing.Point[] triangle =
        [
            new(bounds.Left + bounds.Width / 2, bounds.Top),
            new(bounds.Right, bounds.Bottom),
            new(bounds.Left, bounds.Bottom),
        ];
        graphics.FillPolygon(fillBrush, triangle);
        graphics.DrawPolygon(outlinePen, triangle);

        var stemWidth = Math.Max(1f, bounds.Width / 6f);
        var stemHeight = bounds.Height * 0.40f;
        var stemLeft = bounds.Left + (bounds.Width - stemWidth) / 2f;
        var stemTop = bounds.Top + bounds.Height * 0.38f;
        graphics.FillRectangle(glyphBrush, stemLeft, stemTop, stemWidth, stemHeight);

        var dotDiameter = stemWidth;
        var dotLeft = bounds.Left + (bounds.Width - dotDiameter) / 2f;
        var dotTop = bounds.Top + bounds.Height * 0.82f;
        graphics.FillEllipse(glyphBrush, dotLeft, dotTop, dotDiameter, dotDiameter);
    }

    /// <summary>
    /// Assigns <paramref name="icon"/> to the tray icon only if it differs from the current one,
    /// avoiding redundant <see cref="System.Windows.Forms.NotifyIcon.Icon"/> reassignment that
    /// would otherwise cause visible flicker.
    /// </summary>
    private void SetTrayIcon(Icon? icon)
    {
        if (_trayIcon.Icon != icon)
        {
            _trayIcon.Icon = icon;
        }
    }

    private void UpdateTrayMenuHeaders()
    {
        if (_trayIcon.ContextMenuStrip is not { } menu)
        {
            return;
        }

        menu.Items[0].Text = Loc.Get("Tray.Show");
        menu.Items[1].Text = Loc.Get("Tray.NewDisk");
        menu.Items[3].Text = Loc.Get("Tray.ResetTempDirs");
        menu.Items[4].Text = Loc.Get("Tray.Settings");
        menu.Items[5].Text = Loc.Get("Tray.About");
        menu.Items[7].Text = Loc.Get("Tray.Exit");
    }
}