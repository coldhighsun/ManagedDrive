using ManagedDrive.App.Localization;
using ManagedDrive.App.Models;
using ManagedDrive.App.Services;
using ManagedDrive.App.ViewModels;
using ManagedDrive.App.Views;
using ManagedDrive.Core;
using Microsoft.Win32;
using Serilog;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace ManagedDrive.App;

/// <summary>
/// Application entry point. Owns the <see cref="MountManager"/> lifetime, initialises the
/// system tray icon, auto-mounts persisted disk profiles, and saves settings on exit.
/// </summary>
public partial class App
{
    private const string SingleInstanceMutexName = "Global\\ManagedDrive-4A7C2E1B-9F3D-4B8A-A1C5-3E6D2F0B8C9A";
    // NotifyIcon has no MouseLeave event; Windows keeps resending MouseMove while the cursor
    // rests over the icon, so a short timer that gets restarted on every MouseMove effectively
    // hides the popup shortly after the cursor actually leaves the icon.
    private readonly TimeSpan _showTrayInfoPopup = TimeSpan.FromSeconds(2);
    private readonly DispatcherTimer _timerHiddenTrayInfoPopup = new();
    private bool _isExiting;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private MountManager? _mountManager;
    private SettingsStore? _settings;
    private Mutex? _singleInstanceMutex;
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
        Log.Information("ManagedDrive shutting down.");
        SaveSettings();
        _trayIcon?.Dispose();
        _mainViewModel?.Dispose();
        _mountManager?.Dispose();
        Log.CloseAndFlush();
        if (_singleInstanceMutex != null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }

    private void App_Startup(object sender, StartupEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Development")
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                MessageBox.Show(
                    "ManagedDrive is already running.",
                    "ManagedDrive",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "logs", "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled exception.");
        };

        Log.Information("ManagedDrive starting.");

        _settings = new SettingsStore();
        var config = _settings.Load();
        LanguageManager.Instance.ApplyDefault(config.Language);

        CheckWinFspPrerequisite();

        _mountManager = new MountManager();
        _mainViewModel = new MainViewModel(_mountManager, _settings, config);
        _mainWindow = new MainWindow(_mainViewModel);
        _mainWindow.Closing += MainWindow_Closing;

        SetupTrayIcon();
        SetupUsageWarnings();
        CheckTempDirectoryOnStartup(config);
        AutoMountDisks();

        if (config.StartMinimized)
        {
            _trayIcon!.Visible = true;
        }
        else
        {
            _mainWindow.Topmost = true;
            _mainWindow.Show();
            _mainWindow.Activate();
            _mainWindow.Topmost = false;
        }
    }

    private void AutoMountDisks()
    {
        if (_settings == null || _mainViewModel == null)
        {
            return;
        }

        foreach (var profile in _settings.Load().Disks)
        {
            if (profile.AutoMount)
            {
                _mainViewModel.MountFromProfile(profile);
            }
        }
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
            Log.Warning("TEMP points to {TempPath} on RAM disk {MountPoint} which is not set to auto-mount. Resetting to default.", expanded, mountPoint);
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
            Log.Warning("TEMP points to auto-mount RAM disk {MountPoint}; elevated processes may not access it.", mountPoint);

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

        var installDir = key?.GetValue("InstallDir") as string;
        var dllPath = installDir is not null
            ? Path.Combine(installDir, "bin", "winfsp-msil.dll")
            : null;

        if (dllPath is not null &&
            File.Exists(dllPath) &&
            (FileVersionInfo.GetVersionInfo(dllPath).FileVersion?.StartsWith("2.2.") == true))
        {
            return;
        }

        Log.Warning("WinFsp 2.2.x not detected. InstallDir={InstallDir}", installDir ?? "<none>");

        var result = MessageBox.Show(
            Loc.Get("Msg.WinFspMissingBody"),
            Loc.Get("Msg.WinFspMissingTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Error);

        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo("https://winfsp.dev/rel/") { UseShellExecute = true });
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
        Log.Information("Tray: reset temp directories, success={Success}.", success);
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

    private void ExitApplication()
    {
        if (_mainViewModel != null && IsTempOnAnyDisk(_mainViewModel.Disks))
        {
            ShowMainWindow();

            var dialog = new ConfirmDialog(
                Loc.Get("Msg.TrayExitTempResetTitle"),
                Loc.Get("Msg.TrayExitTempResetBody"))
            {
                Owner = _mainWindow
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            TempDirResetService.Reset();
            Log.Information("Auto-reset temp directory before exiting (tray).");
        }

        _isExiting = true;
        SaveSettings();
        _trayIcon?.Dispose();
        _mainViewModel?.Dispose();
        _mountManager?.Dispose();
        Shutdown();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        _mainWindow!.Hide();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;
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
        Log.Warning("High disk usage on {MountPoint}: {UsedPercent:F1}%.", vm.MountPoint, vm.UsedPercent);
    }

    private void SaveSettings()
    {
        if (_settings == null || _mainViewModel == null)
        {
            return;
        }

        _settings.Save(new AppConfiguration
        {
            RunAtStartup = StartupManager.IsEnabled,
            StartMinimized = _settings.Load().StartMinimized,
            Language = LanguageManager.Instance.SavedLanguage,
            Disks = _mainViewModel.GetProfiles().ToList(),
        });
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

        var iconStream = GetResourceStream(new Uri("pack://application:,,,/ManagedDrive.ico"))!.Stream;

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = new Icon(iconStream),
            ContextMenuStrip = menu,
            Text = "ManagedDrive",
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _trayIcon.MouseMove += (_, _) => Dispatcher.Invoke(() =>
        {
            _trayInfoPopup!.IsOpen = true;
            _timerHiddenTrayInfoPopup.Start();
        });

        _trayInfoPopup = new Popup
        {
            Child = new TrayTooltipView { DataContext = _mainViewModel },
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = GetTrayPopupPlacement,
            AllowsTransparency = true,
            StaysOpen = true,
        };

        _timerHiddenTrayInfoPopup.Interval = _showTrayInfoPopup;
        _timerHiddenTrayInfoPopup.Tick += (_, _) =>
        {
            if (_trayInfoPopup is { IsOpen: true })
            {
                _trayInfoPopup.IsOpen = false;
            }

            _timerHiddenTrayInfoPopup.Stop();
        };

        LanguageManager.Instance.LanguageChanged += (_, _) => UpdateTrayMenuHeaders();
    }

    private CustomPopupPlacement[] GetTrayPopupPlacement(System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset)
    {
        var dpiScale = VisualTreeHelper.GetDpi(_mainWindow ?? this.MainWindow ?? _trayInfoPopup!.Child).DpiScaleX;

        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var workArea = screen.WorkingArea;
        var screenBounds = screen.Bounds;

        var mouseX = cursor.X / dpiScale;
        var mouseY = cursor.Y / dpiScale;
        var workLeft = workArea.Left / dpiScale;
        var workTop = workArea.Top / dpiScale;
        var workRight = workArea.Right / dpiScale;
        var workBottom = workArea.Bottom / dpiScale;

        // Taskbar occupies the gap between the monitor's full bounds and its working area.
        var taskbarOnBottom = workArea.Bottom < screenBounds.Bottom;
        var taskbarOnTop = workArea.Top > screenBounds.Top;
        var taskbarOnLeft = workArea.Left > screenBounds.Left;
        var taskbarOnRight = workArea.Right < screenBounds.Right;

        double x, y;
        if (taskbarOnLeft)
        {
            x = workLeft;
            y = mouseY;
        }
        else if (taskbarOnRight)
        {
            x = workRight - popupSize.Width;
            y = mouseY;
        }
        else if (taskbarOnTop)
        {
            x = mouseX;
            y = workTop;
        }
        else if (taskbarOnBottom)
        {
            x = mouseX;
            y = workBottom - popupSize.Height;
        }
        else
        {
            // No taskbar detected on this monitor (auto-hidden or none): default to below/right of the cursor.
            x = mouseX;
            y = mouseY;
        }

        x = Math.Clamp(x, workLeft, Math.Max(workLeft, workRight - popupSize.Width));
        y = Math.Clamp(y, workTop, Math.Max(workTop, workBottom - popupSize.Height));

        return [new CustomPopupPlacement(new System.Windows.Point(x, y), PopupPrimaryAxis.None)];
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
                }
            }

            if (e.OldItems != null)
            {
                foreach (DiskViewModel vm in e.OldItems)
                {
                    vm.HighUsageWarning -= OnDiskHighUsageWarning;
                }
            }
        };
    }

    private void ShowMainWindow()
    {
        _mainWindow?.Show();
        _mainWindow?.Activate();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
        }
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