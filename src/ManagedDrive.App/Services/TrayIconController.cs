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

    private readonly MainViewModel _mainViewModel;
    private readonly System.Windows.Forms.ToolStripMenuItem _menuShow;
    private readonly System.Windows.Forms.ToolStripMenuItem _menuNewDisk;
    private readonly System.Windows.Forms.ToolStripMenuItem _menuResetTempDirs;
    private readonly System.Windows.Forms.ToolStripMenuItem _menuSettings;
    private readonly System.Windows.Forms.ToolStripMenuItem _menuAbout;
    private readonly System.Windows.Forms.ToolStripMenuItem _menuExit;
    private readonly List<System.Windows.Forms.ToolStripItem> _diskMenuItems = [];
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly Icon _trayIconNormal;
    private bool _blinkOn;
    private bool _isHighUsageActive;

    /// <summary>
    /// Builds the tray icon, its context menu, and the activity-flash timer.
    /// </summary>
    /// <param name="dispatcher">The UI dispatcher; menu/mouse callbacks are marshalled through it.</param>
    /// <param name="iconStream">Stream containing the base <c>.ico</c> resource.</param>
    /// <param name="mainViewModel">
    /// Supplies the live <see cref="MainViewModel.Disks"/> collection and the
    /// open-in-Explorer/save-image/unmount commands for the per-disk submenu rebuilt each time the
    /// context menu opens.
    /// </param>
    /// <param name="onShow">Invoked from the "Show" menu item and double-click.</param>
    /// <param name="onNewDisk">Invoked from the "New Disk" menu item.</param>
    /// <param name="onResetTempDirsAsync">Invoked from the "Reset TEMP Dirs" menu item.</param>
    /// <param name="onSettings">Invoked from the "Settings" menu item.</param>
    /// <param name="onAbout">Invoked from the "About" menu item.</param>
    /// <param name="onExit">Invoked from the "Exit" menu item.</param>
    public TrayIconController(
        Dispatcher dispatcher,
        Stream iconStream,
        MainViewModel mainViewModel,
        Action onShow,
        Action onNewDisk,
        Func<Task> onResetTempDirsAsync,
        Action onSettings,
        Action onAbout,
        Action onExit)
    {
        _dispatcher = dispatcher;
        _mainViewModel = mainViewModel;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        _menuShow = new(Loc.Get("Tray.Show"), null, (_, _) => dispatcher.Invoke(onShow));
        _menuNewDisk = new(Loc.Get("Tray.NewDisk"), null, (_, _) => dispatcher.Invoke(onNewDisk));
        _menuResetTempDirs = new(Loc.Get("Tray.ResetTempDirs"), null, async (_, _) => await dispatcher.InvokeAsync(onResetTempDirsAsync));
        _menuSettings = new(Loc.Get("Tray.Settings"), null, (_, _) => dispatcher.Invoke(onSettings));
        _menuAbout = new(Loc.Get("Tray.About"), null, (_, _) => dispatcher.Invoke(onAbout));
        _menuExit = new(Loc.Get("Tray.Exit"), null, (_, _) => dispatcher.Invoke(onExit));

        menu.Items.Add(_menuShow);
        menu.Items.Add(_menuNewDisk);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_menuResetTempDirs);
        menu.Items.Add(_menuSettings);
        menu.Items.Add(_menuAbout);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_menuExit);
        menu.HandleCreated += (_, _) => ApplyPopupDarkMode(menu);
        menu.Opening += (_, _) => dispatcher.Invoke(() =>
        {
            RebuildDiskMenuItems();
            ContextMenuOpening?.Invoke();
        });

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
    /// Raised right before the tray context menu is shown (right-click). Consumed by
    /// <see cref="TrayTooltipController"/> to dismiss the hover popup so it doesn't sit on top of
    /// or behind the menu.
    /// </summary>
    public event Action? ContextMenuOpening;

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    /// <summary>
    /// <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>: tells DWM to draw a popup window's own chrome (border,
    /// rounded-corner backdrop) in dark colors. Without this, a dark-themed
    /// <see cref="System.Windows.Forms.ContextMenuStrip"/> keeps a light-colored surface around its
    /// owner-drawn content on Windows 11, which washes out white menu text against it.
    /// </summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>
    /// Applies (or clears) <see cref="DwmwaUseImmersiveDarkMode"/> on the popup's native window so
    /// its DWM-drawn chrome matches <see cref="ApplyTrayMenuTheme"/>'s owner-drawn colors. Requires
    /// <paramref name="menu"/>'s handle to already exist; called from its
    /// <see cref="System.Windows.Forms.Control.HandleCreated"/> event and again whenever the theme
    /// changes while the handle is already live.
    /// </summary>
    private static void ApplyPopupDarkMode(System.Windows.Forms.ToolStrip popup)
    {
        if (!popup.IsHandleCreated)
        {
            return;
        }

        var isDark = ThemeManager.Instance.CurrentTheme == "dark";
        var value = isDark ? 1 : 0;
        DwmSetWindowAttribute(popup.Handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

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
        menu.Renderer = new TrayMenuRenderer(isDark);
        menu.BackColor = background;
        menu.ForeColor = foreground;
        foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = foreground;
            if (item is System.Windows.Forms.ToolStripMenuItem { HasDropDownItems: true } parent)
            {
                foreach (System.Windows.Forms.ToolStripItem child in parent.DropDownItems)
                {
                    child.ForeColor = foreground;
                }
            }
        }

        ApplyPopupDarkMode(menu);
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
        _menuShow.Text = Loc.Get("Tray.Show");
        _menuNewDisk.Text = Loc.Get("Tray.NewDisk");
        _menuResetTempDirs.Text = Loc.Get("Tray.ResetTempDirs");
        _menuSettings.Text = Loc.Get("Tray.Settings");
        _menuAbout.Text = Loc.Get("Tray.About");
        _menuExit.Text = Loc.Get("Tray.Exit");
    }

    /// <summary>
    /// Rebuilds the per-disk submenu (one entry per mounted disk, each expanding to Open in
    /// Explorer / Save Image / Unmount) right before the context menu is shown, so it always
    /// reflects the current <see cref="MainViewModel.Disks"/> contents and current language/theme
    /// without needing to track collection or property-changed events between openings.
    /// </summary>
    private void RebuildDiskMenuItems()
    {
        if (_trayIcon.ContextMenuStrip is not { } menu)
        {
            return;
        }

        foreach (var item in _diskMenuItems)
        {
            menu.Items.Remove(item);
            item.Dispose();
        }

        _diskMenuItems.Clear();

        var insertIndex = menu.Items.IndexOf(_menuShow) + 1;
        foreach (var vm in _mainViewModel.Disks)
        {
            var diskItem = new System.Windows.Forms.ToolStripMenuItem($"{vm.MountPoint} ({vm.VolumeLabel})");
            diskItem.DropDownItems.Add(Loc.Get("Btn.OpenInExplorer"), null, (_, _) => _dispatcher.Invoke(() => vm.OpenInExplorerCommand.Execute(null)));
            diskItem.DropDownItems.Add(Loc.Get("Btn.SaveImage"), null, (_, _) => _dispatcher.Invoke(() => _mainViewModel.SaveImageCommand.Execute(vm)));
            diskItem.DropDownItems.Add(Loc.Get("Btn.Unmount"), null, (_, _) => _dispatcher.Invoke(() => _mainViewModel.UnmountCommand.Execute(vm)));
            diskItem.DropDown.HandleCreated += (_, _) => ApplyPopupDarkMode(diskItem.DropDown);

            menu.Items.Insert(insertIndex, diskItem);
            _diskMenuItems.Add(diskItem);
            insertIndex++;
        }

        ApplyTrayMenuTheme();
    }
}
