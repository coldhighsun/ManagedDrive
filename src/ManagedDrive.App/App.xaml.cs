using ManagedDrive.Cli.Core;
using System.Windows.Controls.Primitives;

namespace ManagedDrive.App;

/// <summary>
/// Application entry point. Owns the <see cref="MountManager"/> lifetime, initialises the
/// system tray icon, auto-mounts persisted disk profiles, and saves settings on exit.
/// </summary>
public partial class App
{
    private const string SingleInstanceMutexName = "Global\\ManagedDrive-4A7C2E1B-9F3D-4B8A-A1C5-3E6D2F0B8C9A";

    private readonly DispatcherTimer _timerPollCursor = new();
    private readonly DispatcherTimer _timerShowTrayInfoPopup = new();
    private readonly DispatcherTimer _timerTooltipCooldown = new();
    private CliPipeServer? _cliPipeServer;
    private System.Drawing.Point _iconScreenPoint;
    private bool _isExiting;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private MountManager? _mountManager;
    private SettingsStore? _settings;
    private Mutex? _singleInstanceMutex;
    private bool _tooltipCooldown;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private Popup? _trayInfoPopup;

    private static bool IsTempOnAnyDisk(IEnumerable<DiskViewModel> disks)
    {
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        if (string.IsNullOrEmpty(userTemp))
        {
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(userTemp);
        return disks.Any(d => expanded.StartsWith(d.MountPoint, StringComparison.OrdinalIgnoreCase));
    }

    private void App_Exit(object sender, ExitEventArgs e)
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        _mainViewModel?.SaveSettings();
        _trayIcon?.Dispose();
        _mainViewModel?.Dispose();

        _cliPipeServer?.Dispose();

        // Safety net: if ShutdownAsync already disposed the mount manager, this is a no-op.
        Task.Run(() => _mountManager?.Dispose()).Wait();
        if (_singleInstanceMutex != null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }

    private async void App_Startup(object sender, StartupEventArgs e)
    {
        _singleInstanceMutex = new(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;

            if (e.Args.Length > 0)
            {
                // Launched with CLI-style args (e.g. from the Explorer context menu) while
                // another instance is already running: forward the command to it instead of
                // showing the "already running" dialog.
                if (CliPipeClient.TrySend(e.Args, out var response) && response.ExitCode != 0)
                {
                    MessageBox.Show(response.Message, "ManagedDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                Shutdown();
                return;
            }

            MessageBox.Show(
                "ManagedDrive is already running.",
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = new();
        var config = _settings.Load();
        LanguageManager.Instance.ApplyDefault(config.Language);
        ThemeManager.Instance.ApplyDefault(config.Theme);

        CheckWinFspPrerequisite();

        _mountManager = new();
        SystemEvents.SessionEnding += OnSessionEnding;
        _mainViewModel = new(_mountManager, _settings, config);
        _mainViewModel.ExitRequested += async (_, _) => await ShutdownAsync();
        _mainWindow = new(_mainViewModel);
        _mainWindow.Closing += MainWindow_Closing;

        SetupTrayIcon();
        SetupUsageWarnings();
        CheckTempDirectoryOnStartup(config);

        if (config.StartMinimized)
        {
            _trayIcon!.Visible = true;
            _trayIcon.ShowBalloonTip(
                5000,
                "ManagedDrive",
                Loc.Get("Msg.StartedMinimized"),
                System.Windows.Forms.ToolTipIcon.Info);
        }
        else
        {
            _mainWindow.Topmost = true;
            _mainWindow.Show();
            _mainWindow.Activate();
            _mainWindow.Topmost = false;
        }

        await AutoMountDisksAsync();

        _cliPipeServer = new(_mainViewModel);
        _cliPipeServer.Start();

        if (e.Args.Length > 0)
        {
            // Launched with CLI-style args (e.g. from the Explorer context menu) as the first
            // instance: execute the command directly against this instance's MainViewModel.
            var controller = new MainViewModelCliDiskController(_mainViewModel);
            var result = await CliCommandProcessor.ExecuteAsync(e.Args, controller);
            if (result.ExitCode != 0)
            {
                MessageBox.Show(result.Message, "ManagedDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void ApplyTrayMenuTheme()
    {
        if (_trayIcon?.ContextMenuStrip is not { } menu)
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

    private Task AutoMountDisksAsync()
    {
        if (_settings == null || _mainViewModel == null)
        {
            return Task.CompletedTask;
        }

        var tasks = _settings.Load().Disks
            .Where(p => p.AutoMount)
            .Select(_mainViewModel.MountFromProfileAsync);

        return Task.WhenAll(tasks);
    }

    private void CheckTempDirectoryOnStartup(AppConfiguration config)
    {
        var userTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User);
        if (string.IsNullOrEmpty(userTemp))
        {
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(userTemp);
        if (expanded.Length < 2 || !char.IsLetter(expanded[0]) || expanded[1] != ':')
        {
            return;
        }

        var mountPoint = char.ToUpperInvariant(expanded[0]) + ":";
        var matchingProfile = config.Disks.FirstOrDefault(d =>
            string.Equals(d.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));

        if (matchingProfile == null)
        {
            return;
        }

        if (!matchingProfile.AutoMount)
        {
            // Disk is in profiles but not set to auto-mount — TEMP will be dangling after startup.
            TempDirResetService.Reset();

            MessageBox.Show(
                Loc.Format("Msg.StartupTempReset", expanded),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else
        {
            // Disk is auto-mount and will be available, but elevated processes (e.g. winget) still
            // cannot access user-session WinFsp drives. Warn once so the user is aware.
            if (!config.TempDirCompatWarningShown)
            {
                MessageBox.Show(
                    Loc.Format("Msg.StartupTempAutoMountWarning", expanded),
                    Loc.Get("Msg.SetTempDirWarningTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                _settings!.Save(config with
                {
                    TempDirCompatWarningShown = true
                });
            }
        }
    }

    private void CheckWinFspPrerequisite()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp")
                        ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");

        var dllPath = key?.GetValue("InstallDir") is string installDir
            ? Path.Combine(installDir, "bin", "winfsp-msil.dll")
            : null;

        if (dllPath is not null &&
            File.Exists(dllPath) &&
            (FileVersionInfo.GetVersionInfo(dllPath).FileVersion?.StartsWith("2.2.") == true))
        {
            return;
        }

        var result = MessageBox.Show(
            Loc.Get("Msg.WinFspMissingBody"),
            Loc.Get("Msg.WinFspMissingTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Error);

        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo("https://github.com/winfsp/winfsp/releases/tag/v2.2B2") { UseShellExecute = true });
        }

        Shutdown();
    }

    private async Task ExecuteResetTempDirsFromTray()
    {
        var confirm = new ConfirmDialog(
            Loc.Get("Msg.ResetTempConfirmTitle"),
            Loc.Get("Msg.ResetTempConfirmBody"));

        if (_mainWindow is { IsLoaded: true })
        {
            confirm.Owner = _mainWindow;
        }

        if (confirm.ShowDialog() != true)
        {
            return;
        }

        var success = await Task.Run(TempDirResetService.Reset);
        _trayIcon?.ShowBalloonTip(
            5000,
            "ManagedDrive",
            success ? Loc.Get("Msg.ResetTempSuccess") : Loc.Get("Msg.ResetTempFailed"),
            success ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
    }

    private async void ExitApplication()
    {
        var tempOnRamDisk = _mainViewModel != null && IsTempOnAnyDisk(_mainViewModel.Disks);

        if (_mainViewModel is { Disks.Count: > 0 })
        {
            ShowMainWindow();

            var body = Loc.Get("Msg.ExitConfirmBody");
            if (tempOnRamDisk)
            {
                body = body + "\n\n" + Loc.Get("Msg.ExitTempDirWillBeReset");
            }

            var dialog = new ConfirmDialog(
                Loc.Get("Msg.ExitConfirmTitle"),
                body)
            {
                Owner = _mainWindow
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            if (tempOnRamDisk)
            {
                TempDirResetService.Reset();
            }
        }

        await ShutdownAsync();
    }

    private bool IsInIconRegion(System.Drawing.Point cursor)
    {
        const int halfSize = 16;
        return Math.Abs(cursor.X - _iconScreenPoint.X) <= halfSize
            && Math.Abs(cursor.Y - _iconScreenPoint.Y) <= halfSize;
    }

    private bool IsInPopupRegion(System.Drawing.Point cursor)
    {
        if (_trayInfoPopup?.Child is not FrameworkElement child)
            return false;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(Current.MainWindow ?? new Window());
        var margin = 16.0;
        var left = _trayInfoPopup.HorizontalOffset * dpi.DpiScaleX - margin;
        var top = _trayInfoPopup.VerticalOffset * dpi.DpiScaleY - margin;
        var right = left + child.ActualWidth * dpi.DpiScaleX + margin * 2;
        var bottom = top + child.ActualHeight * dpi.DpiScaleY + margin * 2;
        return cursor.X >= left && cursor.X <= right && cursor.Y >= top && cursor.Y <= bottom;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        _mainWindow!.Hide();
        _trayIcon?.Visible = true;
    }

    private void OnDiskCapacityAdjusted(DiskViewModel vm)
    {
        var originalMb = vm.OriginalCapacityBytesOnLoad!.Value / (1024 * 1024);
        var newMb = vm.Disk.TotalBytes / (1024 * 1024);

        if (_trayIcon != null)
        {
            var title = Loc.Get("Tray.CapacityAdjustedTitle");
            var body = Loc.Format("Tray.CapacityAdjustedBody", vm.VolumeLabel, vm.MountPoint, originalMb, newMb);
            _trayIcon.ShowBalloonTip(5000, title, body, System.Windows.Forms.ToolTipIcon.Warning);
        }

        if (_mainViewModel != null)
        {
            _mainViewModel.StatusText = Loc.Format("Status.CapacityAdjusted", vm.MountPoint, originalMb, newMb);
        }
    }

    private void OnDiskHighUsageWarning(object? sender, EventArgs e)
    {
        if (sender is not DiskViewModel vm || _trayIcon == null)
        {
            return;
        }

        var title = Loc.Get("Tray.HighUsageTitle");
        var body = Loc.Format("Tray.HighUsageBody", vm.VolumeLabel, vm.MountPoint, vm.UsedPercent);
        _trayIcon.ShowBalloonTip(5000, title, body, System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void OnDiskSaveFailed(object? sender, Exception ex)
    {
        if (sender is not DiskViewModel vm)
        {
            return;
        }

        if (_trayIcon != null)
        {
            var title = Loc.Get("Tray.SaveFailedTitle");
            var body = Loc.Format("Tray.SaveFailedBody", vm.VolumeLabel, vm.MountPoint, ex.Message);
            _trayIcon.ShowBalloonTip(5000, title, body, System.Windows.Forms.ToolTipIcon.Error);
        }

        if (_mainViewModel != null)
        {
            _mainViewModel.StatusText = Loc.Format("Status.SaveFailed", vm.MountPoint, ex.Message);
        }
    }

    /// <summary>
    /// Fires when Windows is logging off, shutting down, or restarting. WPF's own
    /// <c>Exit</c> event does not fire in this case, and the OS may kill the process shortly
    /// after this callback returns, so save every mounted disk's image synchronously and
    /// without unmounting (unmounting is unnecessary here and would risk exceeding the
    /// shutdown time budget).
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        if (_mountManager == null)
        {
            return;
        }

        foreach (var disk in _mountManager.GetAll())
        {
            try
            {
                disk.SaveToImageSafe();
            }
            catch
            {
                // Best-effort, matches RamDisk.Dispose()/TryAutoSave() swallow pattern.
            }
        }
    }

    private void PositionTrayPopup()
    {
        var workArea = SystemParameters.WorkArea;
        var child = _trayInfoPopup!.Child as FrameworkElement;
        child?.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        var popupHeight = child?.DesiredSize.Height ?? 80;
        var popupWidth = child?.DesiredSize.Width ?? 200;

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(Current.MainWindow ?? new Window());
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

    private void SetupTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.Get("Tray.Show"), null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add(Loc.Get("Tray.NewDisk"), null, (_, _) => Dispatcher.Invoke(ShowMainWindowAndCreate));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.Get("Tray.ResetTempDirs"), null, async (_, _) => await Dispatcher.InvokeAsync(ExecuteResetTempDirsFromTray));
        menu.Items.Add(Loc.Get("Tray.Settings"), null, (_, _) => Dispatcher.Invoke(ShowMainWindowAndSettings));
        menu.Items.Add(Loc.Get("Tray.About"), null, (_, _) => Dispatcher.Invoke(ShowAboutDialog));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.Get("Tray.Exit"), null, (_, _) => Dispatcher.Invoke(ExitApplication));

        var iconStream = GetResourceStream(new("pack://application:,,,/ManagedDrive.ico"))!.Stream;

        _trayIcon = new()
        {
            Icon = new(iconStream),
            ContextMenuStrip = menu,
            Text = "",
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _trayIcon.MouseMove += (_, _) => Dispatcher.Invoke(() =>
        {
            _iconScreenPoint = System.Windows.Forms.Cursor.Position;
            if (!_trayInfoPopup!.IsOpen && !_tooltipCooldown)
                _timerShowTrayInfoPopup.Start();
        });

        _trayInfoPopup = new()
        {
            Child = new TrayTooltipView { DataContext = _mainViewModel },
            Placement = PlacementMode.AbsolutePoint,
            AllowsTransparency = true,
            StaysOpen = true,
        };

        _timerShowTrayInfoPopup.Interval = TimeSpan.FromMilliseconds(500);
        _timerShowTrayInfoPopup.Tick += (_, _) =>
        {
            _timerShowTrayInfoPopup.Stop();
            PositionTrayPopup();
            _trayInfoPopup!.IsOpen = true;
            _timerPollCursor.Start();
        };

        _timerPollCursor.Interval = TimeSpan.FromMilliseconds(200);
        _timerPollCursor.Tick += (_, _) =>
        {
            if (_trayInfoPopup is not { IsOpen: true })
            {
                _timerPollCursor.Stop();
                return;
            }
            var cur = System.Windows.Forms.Cursor.Position;
            if (IsInIconRegion(cur) || IsInPopupRegion(cur))
                return;
            _trayInfoPopup.IsOpen = false;
            _timerPollCursor.Stop();
            _tooltipCooldown = true;
            _timerTooltipCooldown.Start();
        };

        _timerTooltipCooldown.Interval = TimeSpan.FromMilliseconds(300);
        _timerTooltipCooldown.Tick += (_, _) =>
        {
            _tooltipCooldown = false;
            _timerTooltipCooldown.Stop();
        };

        LanguageManager.Instance.LanguageChanged += (_, _) => UpdateTrayMenuHeaders();
        ApplyTrayMenuTheme();
        ThemeManager.Instance.ThemeChanged += (_, _) => Dispatcher.Invoke(ApplyTrayMenuTheme);
    }

    private void SetupUsageWarnings()
    {
        if (_mainViewModel == null)
        {
            return;
        }

        _mainViewModel.Disks.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (DiskViewModel vm in e.NewItems)
                {
                    vm.HighUsageWarning += OnDiskHighUsageWarning;
                    vm.SaveFailed += OnDiskSaveFailed;

                    if (vm is { CapacityAdjustedOnLoad: true, Disk.Options.SourceArchivePath: null })
                    {
                        OnDiskCapacityAdjusted(vm);
                    }
                }
            }

            if (e.OldItems != null)
            {
                foreach (DiskViewModel vm in e.OldItems)
                {
                    vm.HighUsageWarning -= OnDiskHighUsageWarning;
                    vm.SaveFailed -= OnDiskSaveFailed;
                }
            }
        };
    }

    private void ShowAboutDialog()
    {
        var dialog = new AboutDialog();
        if (_mainWindow is { IsLoaded: true })
        {
            dialog.Owner = _mainWindow;
        }

        dialog.ShowDialog();
    }

    private void ShowMainWindow()
    {
        _mainWindow?.Show();
        _mainWindow?.Activate();
        _trayIcon?.Visible = false;
    }

    private void ShowMainWindowAndCreate()
    {
        ShowMainWindow();
        _mainViewModel?.CreateDiskCommand.Execute(null);
    }

    private void ShowMainWindowAndSettings()
    {
        ShowMainWindow();
        _mainViewModel?.SettingsCommand.Execute(null);
    }

    private async Task ShutdownAsync()
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        _isExiting = true;

        if (_mainViewModel != null)
        {
            _mainViewModel.IsExiting = true;
            ShowMainWindow();
        }

        _cliPipeServer?.Dispose();
        _mainViewModel?.SaveSettings();
        _trayIcon?.Dispose();
        _mainViewModel?.Dispose();
        await Task.Run(() => _mountManager?.Dispose());
        Shutdown();
    }

    private void UpdateTrayMenuHeaders()
    {
        if (_trayIcon?.ContextMenuStrip is not { } menu)
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