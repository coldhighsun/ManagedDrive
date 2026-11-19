namespace ManagedDrive.App.Services;

/// <summary>
/// Owns the system tray icon: its activity lights and alert badge (decided by
/// <see cref="TrayIconStateMachine"/>, drawn by <see cref="TrayIconRenderer"/>), the context menu
/// and its theme/language wiring. Extracted from <see cref="App"/> so
/// tray-icon concerns live in one place, separate from tooltip popup handling
/// (<see cref="TrayTooltipController"/>) and application lifecycle.
/// </summary>
public sealed class TrayIconController : IDisposable, ITrayHoverSource
{
    /// <summary>
    /// How long a balloon tip stays up. Windows 10+ ignores this in favour of the system's
    /// notification duration, but older shells still honour it.
    /// </summary>
    private static readonly TimeSpan BalloonTipTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The padding of a menu item on every side at 96 DPI, in pixels. The vertical part is the
    /// spacing between neighbouring items, which WinForms' default (1 px) leaves too tight; the
    /// horizontal part indents the text, where the default is 0.
    /// </summary>
    private const int MenuItemPadding = 4;

    /// <summary>
    /// The UI dispatcher; menu, mouse and activity callbacks are marshalled through it.
    /// </summary>
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// Fires when <see cref="_iconState"/> says the icon changes next by itself (a light going out,
    /// a pulse phase ending); stopped while nothing is due.
    /// </summary>
    private readonly DispatcherTimer _iconTimer = new();

    /// <summary>
    /// Decides which lights and which badge the icon shows.
    /// </summary>
    private readonly TrayIconStateMachine _iconState = new();

    /// <summary>
    /// The bytes of the application's <c>.ico</c> file, kept to draw the icon again at a new size.
    /// </summary>
    private readonly byte[] _iconBytes;

    /// <summary>
    /// Draws the icons at the current notification-area size; replaced when the size changes.
    /// </summary>
    private TrayIconRenderer _renderer;

    /// <summary>
    /// Whether <see cref="Dispose"/> ran; icon updates still queued on the dispatcher then do nothing.
    /// </summary>
    private bool _disposed;

    private readonly MainViewModel _mainViewModel;

    /// <summary>
    /// Tracks whether the tray icon is only visible so balloon tips could be shown (see
    /// <see cref="ShowBalloonTip"/>) and must be hidden again once the last of them closes.
    /// </summary>
    private readonly BalloonOnlyIconTracker _balloonOnlyIcon = new();
    private readonly System.Windows.Forms.ToolStripMenuItem _menuShow;
    private readonly System.Windows.Forms.ToolStripMenuItem _menuNewDisk;
    private readonly System.Windows.Forms.ToolStripMenuItem _menuRestoreEnv;
    private readonly System.Windows.Forms.ToolStripMenuItem _menuSettings;
    private readonly System.Windows.Forms.ToolStripMenuItem _menuAbout;
    private readonly System.Windows.Forms.ToolStripMenuItem _menuExit;
    private readonly List<System.Windows.Forms.ToolStripItem> _diskMenuItems = [];

    /// <summary>
    /// Restores a group of redirected environment variables; <c>null</c> restores all groups.
    /// </summary>
    private readonly Func<EnvRestoreGroup?, Task> _onRestoreEnvAsync;

    /// <summary>
    /// The palette (and whether it was the dark theme's) the menu was last styled with; compared with the
    /// application's resources when the menu opens.
    /// </summary>
    private (bool IsDark, TrayMenuPalette Palette)? _appliedMenuTheme;

    /// <summary>
    /// The handlers on <see cref="ThemeManager"/> and <see cref="StyleManager"/>, kept so that <see cref="Dispose"/>
    /// can remove them from those longer-lived objects.
    /// </summary>
    private readonly EventHandler _onThemeOrStyleChanged;

    /// <summary>
    /// Takes the user where a clicked balloon tip points.
    /// </summary>
    private readonly Action<NotificationClickTarget> _onBalloonClicked;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;

    /// <summary>
    /// The target of the latest balloon tip.
    /// </summary>
    private readonly BalloonClickTargetTracker _balloonClickTarget = new();

    /// <summary>
    /// Builds the tray icon, its context menu, and the timer that animates the icon.
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
    /// <param name="onRestoreEnvAsync">
    /// Invoked from an item of the "Restore environment variables" submenu with the group to restore,
    /// or <c>null</c> for the item that restores all groups.
    /// </param>
    /// <param name="onSettings">Invoked from the "Settings" menu item.</param>
    /// <param name="onAbout">Invoked from the "About" menu item.</param>
    /// <param name="onExit">Invoked from the "Exit" menu item.</param>
    /// <param name="onBalloonClicked">Invoked when a balloon tip shown with a click target is clicked.</param>
    public TrayIconController(
        Dispatcher dispatcher,
        Stream iconStream,
        MainViewModel mainViewModel,
        Action onShow,
        Action onNewDisk,
        Func<EnvRestoreGroup?, Task> onRestoreEnvAsync,
        Action onSettings,
        Action onAbout,
        Action onExit,
        Action<NotificationClickTarget> onBalloonClicked)
    {
        _dispatcher = dispatcher;
        _mainViewModel = mainViewModel;
        _onBalloonClicked = onBalloonClicked;

        var menu = new TrayContextMenu();
        _menuShow = new(Loc.Get("Tray.Show"), null, (_, _) => dispatcher.Invoke(onShow));
        _menuNewDisk = new(Loc.Get("Tray.NewDisk"), null, (_, _) => dispatcher.Invoke(onNewDisk));
        _menuRestoreEnv = new(Loc.Get("Tray.RestoreEnv")) { DropDown = new TraySubmenu() };
        _onRestoreEnvAsync = onRestoreEnvAsync;
        _menuRestoreEnv.DropDown.HandleCreated += (_, _) => ApplyPopupChrome(_menuRestoreEnv.DropDown);
        _menuSettings = new(Loc.Get("Tray.Settings"), null, (_, _) => dispatcher.Invoke(onSettings));
        _menuAbout = new(Loc.Get("Tray.About"), null, (_, _) => dispatcher.Invoke(onAbout));
        _menuExit = new(Loc.Get("Tray.Exit"), null, (_, _) => dispatcher.Invoke(onExit));

        menu.Items.Add(_menuShow);
        menu.Items.Add(_menuNewDisk);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_menuRestoreEnv);
        menu.Items.Add(_menuSettings);
        menu.Items.Add(_menuAbout);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_menuExit);
        menu.HandleCreated += (_, _) => ApplyPopupChrome(menu);
        menu.Opening += (_, _) => dispatcher.Invoke(() =>
        {
            RefreshTrayMenuThemeIfChanged();
            RebuildRestoreEnvItems();
            RebuildDiskMenuItems();
            ContextMenuOpening?.Invoke();
        });

        using (var buffer = new MemoryStream())
        {
            iconStream.CopyTo(buffer);
            _iconBytes = buffer.ToArray();
        }

        _renderer = new(_iconBytes, WindowNativeMethods.GetTrayIconSize());

        _trayIcon = new()
        {
            Icon = _renderer.GetIcon(TrayIconVisual.Idle),
            ContextMenuStrip = menu,
            Text = "",
            Visible = false,
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                dispatcher.Invoke(onShow);
            }
        };
        _trayIcon.BalloonTipClosed += (_, _) => OnBalloonTipClosed();
        _trayIcon.BalloonTipClicked += (_, _) => OnBalloonTipClicked();
        _trayIcon.MouseMove += (_, _) =>
        {
            var point = System.Windows.Forms.Cursor.Position;
            dispatcher.Invoke(() => MouseMoved?.Invoke(point));
        };

        _iconTimer.Tick += (_, _) => UpdateIcon();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        LanguageManager.Instance.LanguageChanged += (_, _) => UpdateTrayMenuHeaders();
        ApplyTrayMenuTheme();
        _onThemeOrStyleChanged = (_, _) => dispatcher.Invoke(ApplyTrayMenuTheme);
        ThemeManager.Instance.ThemeChanged += _onThemeOrStyleChanged;
        StyleManager.Instance.StyleChanged += _onThemeOrStyleChanged;
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
    /// Gets a value indicating whether the tray context menu is currently showing. Read by
    /// <see cref="TrayTooltipController"/> so the hover popup stays away while it is.
    /// </summary>
    public bool IsContextMenuVisible => _trayIcon.ContextMenuStrip?.Visible ?? false;

    /// <summary>
    /// Gets or sets whether the tray icon is visible.
    /// </summary>
    public bool Visible
    {
        get => _trayIcon.Visible;
        set
        {
            _balloonOnlyIcon.Reset();
            _trayIcon.Visible = value;
        }
    }

    /// <summary>
    /// Stops the icon timer and releases the tray icon, then the drawn icons and their HICONs.
    /// </summary>
    public void Dispose()
    {
        // Set first: the final saves run after this and can still queue icon updates (a failed save
        // raises an alert), which must find nothing to draw with rather than a released renderer.
        _disposed = true;
        ThemeManager.Instance.ThemeChanged -= _onThemeOrStyleChanged;
        StyleManager.Instance.StyleChanged -= _onThemeOrStyleChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _iconTimer.Stop();
        _trayIcon.Dispose();
        _renderer.Dispose();
    }

    /// <summary>
    /// Handler for <see cref="MountManager.ActivityDetected"/>. Runs on a timer thread, so the icon
    /// update is dispatched to the UI thread. Lights the read and the write light independently;
    /// each goes out <see cref="TrayIconStateMachine.ActivityHold"/> after the last access it saw.
    /// </summary>
    /// <param name="hadRead">Whether anything was read since the last poll.</param>
    /// <param name="hadWrite">Whether anything was written since the last poll.</param>
    public void OnActivityDetected(bool hadRead, bool hadWrite)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _iconState.OnActivity(hadRead, hadWrite, DateTimeOffset.UtcNow);
            UpdateIcon();
        });
    }

    /// <summary>
    /// Sets the alert badge: none, a steady warning, or a critical badge that pulses for
    /// <see cref="TrayIconStateMachine.CriticalPulseDuration"/> and then stays on. The caller
    /// (<see cref="DiskNotificationService"/>) reduces every disk's alerts to this one level.
    /// </summary>
    /// <param name="level">The most urgent alert of all disks and the system.</param>
    /// <param name="restartPulse">
    /// Whether a new failure arrived, so a critical badge pulses again even if it already was critical.
    /// </param>
    public void SetAlertLevel(TrayAlertLevel level, bool restartPulse = false)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var now = DateTimeOffset.UtcNow;
            _iconState.SetAlertLevel(level, now);
            if (restartPulse)
            {
                _iconState.RestartPulse(now);
            }

            UpdateIcon();
        });
    }

    /// <summary>
    /// Shows what <see cref="_iconState"/> decides for now and arms <see cref="_iconTimer"/> for its
    /// next change by itself. Does nothing once disposed.
    /// </summary>
    private void UpdateIcon()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        SetTrayIcon(_renderer.GetIcon(_iconState.GetVisual(now)));

        _iconTimer.Stop();
        if (_iconState.GetNextChange(now) is { } delay)
        {
            _iconTimer.Interval = delay;
            _iconTimer.Start();
        }
    }

    /// <summary>
    /// Handles a change of the display configuration, which may change the notification-area icon size.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="e">The event data.</param>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(RefreshIconSize);

    /// <summary>
    /// Handles a change of user preferences; changing only the display scale may arrive here
    /// without a display-settings change.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="e">The event data.</param>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => _dispatcher.BeginInvoke(RefreshIconSize);

    /// <summary>
    /// Draws the icons again when the notification-area icon size changed with the DPI of the
    /// primary monitor. The new icon is shown before the old renderer releases its icons, so the
    /// tray never points at a destroyed handle. Does nothing once disposed.
    /// </summary>
    private void RefreshIconSize()
    {
        if (_disposed)
        {
            return;
        }

        var size = WindowNativeMethods.GetTrayIconSize();
        if (size == _renderer.Layout.Size)
        {
            return;
        }

        var old = _renderer;
        _renderer = new(_iconBytes, size);
        SetTrayIcon(_renderer.GetIcon(_iconState.GetVisual(DateTimeOffset.UtcNow)));
        old.Dispose();
    }

    /// <summary>
    /// Shows a balloon tip from the tray icon. Windows only shows balloons for a visible icon, and
    /// the icon is hidden while the main window is open — including when that window is
    /// minimized to the taskbar, where its status bar can't be seen either — so a hidden icon is
    /// shown for the balloon's lifetime and hidden again when the last open balloon closes.
    /// </summary>
    /// <param name="title">The balloon tip title.</param>
    /// <param name="body">The balloon tip text.</param>
    /// <param name="icon">The balloon tip icon.</param>
    /// <param name="clickTarget">Where clicking the balloon leads; <c>null</c> when it only informs.</param>
    public void ShowBalloonTip(
        string title,
        string body,
        System.Windows.Forms.ToolTipIcon icon,
        NotificationClickTarget? clickTarget = null)
    {
        _balloonClickTarget.OnBalloonShowing(clickTarget);

        if (_balloonOnlyIcon.OnBalloonShowing(_trayIcon.Visible))
        {
            _trayIcon.Visible = true;
        }

        _trayIcon.ShowBalloonTip((int)BalloonTipTimeout.TotalMilliseconds, title, body, icon);
    }

    /// <summary>
    /// Hides the tray icon again if <see cref="ShowBalloonTip"/> only showed it for balloons.
    /// Called when the main window comes back into view, in case a balloon's close notification
    /// never arrives.
    /// </summary>
    public void HideIfShownForBalloonOnly()
    {
        if (_balloonOnlyIcon.Reset())
        {
            _trayIcon.Visible = false;
        }
    }

    /// <summary>
    /// Hides the tray icon once the last balloon it was only shown for has closed or been clicked.
    /// </summary>
    private void OnBalloonTipClosed()
    {
        if (_balloonOnlyIcon.OnBalloonClosed())
        {
            _trayIcon.Visible = false;
        }
    }

    /// <summary>
    /// Handles a click on a balloon tip. The target runs through the dispatcher queue, not here: it
    /// can open a modal dialog, which must not block the tray icon's message callback.
    /// </summary>
    private void OnBalloonTipClicked()
    {
        OnBalloonTipClosed();

        if (_balloonClickTarget.TakeForClick() is { } target)
        {
            _dispatcher.BeginInvoke(() => _onBalloonClicked(target));
        }
    }

    /// <summary>
    /// Applies the DWM-drawn chrome of a popup's native window so it matches
    /// <see cref="ApplyTrayMenuTheme"/>'s owner-drawn colors. It sets three attributes:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// The dark or light frame (<see cref="WindowNativeMethods.DwmwaUseImmersiveDarkMode"/>); without
    /// it a dark-themed <see cref="System.Windows.Forms.ContextMenuStrip"/> keeps a light-colored
    /// surface around its owner-drawn content on Windows 11.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// The native rounded corners (<see cref="WindowNativeMethods.DwmwaWindowCornerPreference"/>).
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// A border in the menu's border color that follows the rounding
    /// (<see cref="WindowNativeMethods.DwmwaBorderColor"/>).
    /// </description>
    /// </item>
    /// </list>
    /// Requires <paramref name="popup"/>'s handle to already exist; called from its
    /// <see cref="System.Windows.Forms.Control.HandleCreated"/> event and again whenever the theme
    /// changes while the handle is already live. The return values are ignored on purpose: the
    /// corner and border attributes do not exist before Windows 11, where the menu simply keeps its
    /// square look.
    /// </summary>
    /// <param name="popup">The popup whose native window is updated.</param>
    private void ApplyPopupChrome(System.Windows.Forms.ToolStrip popup)
    {
        if (!popup.IsHandleCreated)
        {
            return;
        }

        // The palette the menu was last styled with (see ApplyTrayMenuTheme); the first popup can be created before that.
        var isDark = ThemeManager.Instance.CurrentTheme == "dark";
        var palette = _appliedMenuTheme?.Palette ?? TrayMenuPalette.FromResources(isDark);
        WindowNativeMethods.SetDwmAttribute(popup.Handle, WindowNativeMethods.DwmwaUseImmersiveDarkMode, isDark ? 1 : 0);
        WindowNativeMethods.SetDwmAttribute(popup.Handle, WindowNativeMethods.DwmwaWindowCornerPreference, WindowNativeMethods.DwmwcpRound);
        WindowNativeMethods.SetDwmAttribute(
            popup.Handle,
            WindowNativeMethods.DwmwaBorderColor,
            System.Drawing.ColorTranslator.ToWin32(palette.Border));
    }

    /// <summary>
    /// Gets the padding of a menu item for a DPI: <see cref="MenuItemPadding"/> pixels at 96 DPI,
    /// scaled up, because a padding assigned at runtime is taken as device pixels.
    /// </summary>
    /// <param name="deviceDpi">The DPI of the menu.</param>
    /// <returns>The padding on all four sides.</returns>
    internal static System.Windows.Forms.Padding GetItemPadding(int deviceDpi) =>
        new((int)Math.Round(MenuItemPadding * deviceDpi / 96.0));

    /// <summary>
    /// Gives menu items, and the items of their submenus, the menu's foreground color and spacing.
    /// Run for every item when the theme is applied, and for the items that are rebuilt each time
    /// the menu opens, so that opening the menu does not have to re-apply the whole theme. The
    /// color and padding are worked out once for all the items.
    /// </summary>
    /// <param name="items">
    /// The <see cref="System.Windows.Forms.ToolStripItem"/>s to style; taken as the non-generic
    /// <see cref="System.Collections.IEnumerable"/> because WinForms' item collections are not generic.
    /// </param>
    private void StyleMenuItems(System.Collections.IEnumerable items)
    {
        if (_trayIcon.ContextMenuStrip is not { } menu)
        {
            return;
        }

        var padding = GetItemPadding(menu.DeviceDpi);
        var margin = TrayMenuMetrics.GetItemMargin(menu.DeviceDpi);
        foreach (System.Windows.Forms.ToolStripItem item in items)
        {
            StyleMenuItem(item, menu.ForeColor, padding, margin, menu.Renderer);
        }
    }

    /// <summary>
    /// Gives one menu item, and the items of its submenu, a foreground color and, for menu items,
    /// a padding and a margin; separators keep their own height. A submenu gets the menu's renderer,
    /// which a submenu made by WinForms inherits but a <see cref="TraySubmenu"/> does not.
    /// </summary>
    /// <param name="item">The item to style.</param>
    /// <param name="foreground">The foreground color.</param>
    /// <param name="padding">The padding of a menu item.</param>
    /// <param name="margin">The margin of a menu item.</param>
    /// <param name="renderer">The renderer of the menu.</param>
    private static void StyleMenuItem(
        System.Windows.Forms.ToolStripItem item,
        Color foreground,
        System.Windows.Forms.Padding padding,
        System.Windows.Forms.Padding margin,
        System.Windows.Forms.ToolStripRenderer renderer)
    {
        item.ForeColor = foreground;
        if (item is not System.Windows.Forms.ToolStripMenuItem menuItem)
        {
            return;
        }

        menuItem.Padding = padding;
        menuItem.Margin = margin;

        // Only ask for the drop-down of an item that has one: reading it creates it. The restore item has
        // one while it has no items yet, because they are added each time the menu opens.
        if (!menuItem.HasDropDown)
        {
            return;
        }

        // The menu has no icons, and neither has its submenu: without this it keeps an empty strip at the left.
        if (menuItem.DropDown is System.Windows.Forms.ToolStripDropDownMenu submenu)
        {
            submenu.ShowImageMargin = false;
            submenu.ShowCheckMargin = false;
            submenu.Renderer = renderer;
        }

        foreach (System.Windows.Forms.ToolStripItem child in menuItem.DropDownItems)
        {
            StyleMenuItem(child, foreground, padding, margin, renderer);
        }
    }

    /// <summary>
    /// Styles the menu again if the application's colors are not the ones it was styled with. A theme or style change
    /// styles the menu too, but it can do so before the style's palette has replaced the old one in the resources;
    /// looking again right before the menu is shown always sees the final colors, and costs nothing when nothing changed.
    /// </summary>
    private void RefreshTrayMenuThemeIfChanged()
    {
        var isDark = ThemeManager.Instance.CurrentTheme == "dark";
        if (_appliedMenuTheme != (isDark, TrayMenuPalette.FromResources(isDark)))
        {
            ApplyTrayMenuTheme();
        }
    }

    /// <summary>
    /// Applies the current theme to the tray menu: the renderer, the colors, the item styling and
    /// the DWM chrome. Run once at construction and again whenever the theme changes; the items
    /// that are rebuilt each time the menu opens are styled by <see cref="StyleMenuItems"/> instead.
    /// </summary>
    private void ApplyTrayMenuTheme()
    {
        if (_trayIcon.ContextMenuStrip is not { } menu)
        {
            return;
        }

        var isDark = ThemeManager.Instance.CurrentTheme == "dark";
        var palette = TrayMenuPalette.FromResources(isDark);
        _appliedMenuTheme = (isDark, palette);

        menu.ShowImageMargin = false;
        menu.Renderer = new TrayMenuRenderer(isDark, palette);
        menu.BackColor = palette.Background;
        menu.ForeColor = palette.Foreground;
        StyleMenuItems(menu.Items);
        ApplyPopupChrome(menu);
    }

    /// <summary>
    /// Assigns <paramref name="icon"/> to the tray icon only if it differs from the current one,
    /// avoiding redundant <see cref="System.Windows.Forms.NotifyIcon.Icon"/> reassignment that
    /// would otherwise cause visible flicker.
    /// </summary>
    private void SetTrayIcon(Icon icon)
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
        _menuRestoreEnv.Text = Loc.Get("Tray.RestoreEnv");
        _menuSettings.Text = Loc.Get("Tray.Settings");
        _menuAbout.Text = Loc.Get("Tray.About");
        _menuExit.Text = Loc.Get("Tray.Exit");
    }

    /// <summary>
    /// Rebuilds the "Restore environment variables" submenu right before the context menu is shown:
    /// one item per group that can be restored and, when there are several, an item for all of them.
    /// </summary>
    private void RebuildRestoreEnvItems()
    {
        // Disposing an item removes it from the collection, so take a copy to walk.
        foreach (var item in _menuRestoreEnv.DropDownItems.Cast<System.Windows.Forms.ToolStripItem>().ToList())
        {
            item.Dispose();
        }

        var readable = _mainViewModel.TryGetEnvRestoreGroups(out var groups);
        foreach (var entry in EnvRestoreMenuEntry.Build(groups, readFailed: !readable))
        {
            switch (entry.Kind)
            {
                case EnvRestoreMenuEntryKind.Separator:
                    _menuRestoreEnv.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
                    break;
                case EnvRestoreMenuEntryKind.Nothing:
                case EnvRestoreMenuEntryKind.Unreadable:
                    _menuRestoreEnv.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem(entry.GetText()) { Enabled = false });
                    break;
                default:
                    var group = entry.Group;
                    _menuRestoreEnv.DropDownItems.Add(entry.GetText(), null, (_, _) => RunRestoreEnv(group));
                    break;
            }
        }

        StyleMenuItems(_menuRestoreEnv.DropDownItems);
    }

    /// <summary>
    /// Runs a restore chosen from the submenu on the UI thread.
    /// </summary>
    /// <param name="group">The group to restore; <c>null</c> restores all groups.</param>
    private async void RunRestoreEnv(EnvRestoreGroup? group)
    {
        try
        {
            await _dispatcher.InvokeAsync(() => _onRestoreEnvAsync(group)).Task.Unwrap();
        }
        catch (Exception ex)
        {
            // An async void handler would hand this to the global crash handler; the user gets the failure message instead.
            AppLog.CreateLogger<TrayIconController>().LogError(ex, "Restoring environment variables from the tray failed.");
            ShowBalloonTip(
                "ManagedDrive",
                Loc.Get("Msg.RestoreEnvFailed"),
                System.Windows.Forms.ToolTipIcon.Warning,
                NotificationClickTarget.ShowWindow);
        }
    }

    /// <summary>
    /// Rebuilds the per-disk submenu (one entry per mounted disk, each expanding to Open in
    /// Explorer / Save Image / Unmount) right before the context menu is shown, so it always
    /// reflects the current <see cref="MainViewModel.Disks"/> contents and current language
    /// without needing to track collection or property-changed events between openings. The new
    /// items get the current theme's colors and spacing; the theme itself is applied when it changes.
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

        // After "New Disk", so that it stays right below "Show" and the disks follow the two.
        var insertIndex = menu.Items.IndexOf(_menuNewDisk) + 1;
        foreach (var vm in _mainViewModel.Disks)
        {
            var diskItem = new System.Windows.Forms.ToolStripMenuItem($"{vm.MountPoint} ({vm.VolumeLabel})") { DropDown = new TraySubmenu() };
            diskItem.DropDownItems.Add(Loc.Get("Btn.OpenInExplorer"), null, (_, _) => _dispatcher.Invoke(() => vm.OpenInExplorerCommand.Execute(null)));
            diskItem.DropDownItems.Add(Loc.Get("Btn.SaveImage"), null, (_, _) => _dispatcher.Invoke(() => _mainViewModel.SaveImageCommand.Execute(vm)));
            diskItem.DropDownItems.Add(Loc.Get("Btn.Unmount"), null, (_, _) => _dispatcher.Invoke(() => _mainViewModel.UnmountCommand.Execute(vm)));
            diskItem.DropDown.HandleCreated += (_, _) => ApplyPopupChrome(diskItem.DropDown);

            menu.Items.Insert(insertIndex, diskItem);
            _diskMenuItems.Add(diskItem);
            insertIndex++;
        }

        StyleMenuItems(_diskMenuItems);
    }
}
