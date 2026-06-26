using H.NotifyIcon;
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
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ManagedDrive.App;

/// <summary>
/// Application entry point. Owns the <see cref="MountManager"/> lifetime, initialises the
/// system tray icon, auto-mounts persisted disk profiles, and saves settings on exit.
/// </summary>
public partial class App
{
    private const string SingleInstanceMutexName = "Global\\ManagedDrive-4A7C2E1B-9F3D-4B8A-A1C5-3E6D2F0B8C9A";
    private readonly TimeSpan _showTrayInfoPopup = TimeSpan.FromSeconds(3);
    private readonly DispatcherTimer _timerHiddenTrayInfoPopup = new();
    private bool _isExiting;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private MountManager? _mountManager;
    private SettingsStore? _settings;
    private Mutex? _singleInstanceMutex;
    private TaskbarIcon? _trayIcon;
    private Popup? _trayInfoPopup;

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
        _mainViewModel = new MainViewModel(_mountManager, _settings);
        _mainWindow = new MainWindow(_mainViewModel);
        _mainWindow.Closing += MainWindow_Closing;

        SetupTrayIcon();
        SetupUsageWarnings();
        CheckTempDirectoryOnStartup(config);
        AutoMountDisks();

        if (config.StartMinimized)
        {
            _trayIcon!.Visibility = Visibility.Visible;
        }
        else
        {
            _mainWindow.Show();
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

        if (matchingProfile == null || matchingProfile.AutoMount)
        {
            return;
        }

        // Disk is in profiles but not set to auto-mount — TEMP will be dangling after startup.
        Log.Warning("TEMP points to {TempPath} on RAM disk {MountPoint} which is not set to auto-mount. Resetting to default.", expanded, mountPoint);
        TempDirResetService.Reset();

        MessageBox.Show(
            Loc.Format("Msg.StartupTempReset", expanded),
            "ManagedDrive",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
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
            (FileVersionInfo.GetVersionInfo(dllPath).FileVersion?.StartsWith("2.1.") == true))
        {
            return;
        }

        Log.Warning("WinFsp 2.1.x not detected. InstallDir={InstallDir}", installDir ?? "<none>");

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

    private void ExitApplication()
    {
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
        _trayIcon?.Visibility = Visibility.Visible;
    }

    private void OnDiskHighUsageWarning(object? sender, EventArgs e)
    {
        if (sender is not DiskViewModel vm || _trayIcon == null)
        {
            return;
        }

        var title = Loc.Get("Tray.HighUsageTitle");
        var body = Loc.Format("Tray.HighUsageBody", vm.VolumeLabel, vm.MountPoint, vm.UsedPercent);
        _trayIcon.ShowNotification(title, body, H.NotifyIcon.Core.NotificationIcon.Warning);
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
        var showItem = new MenuItem { Header = Loc.Get("Tray.Show") };
        showItem.Click += (_, _) => ShowMainWindow();
        var newDiskItem = new MenuItem { Header = Loc.Get("Tray.NewDisk") };
        newDiskItem.Click += (_, _) => ShowMainWindowAndCreate();
        var exitItem = new MenuItem { Header = Loc.Get("Tray.Exit") };
        exitItem.Click += (_, _) => ExitApplication();

        var menu = new ContextMenu();
        menu.Items.Add(showItem);
        menu.Items.Add(newDiskItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _trayIcon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/ManagedDrive.ico")),
            ContextMenu = menu,
            ToolTipText = "ManagedDrive",
            Visibility = Visibility.Hidden,
        };
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
        _trayIcon.ForceCreate();

        _trayInfoPopup = new Popup
        {
            Child = new TrayTooltipView { DataContext = _mainViewModel },
            Placement = PlacementMode.Mouse,
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

        _trayIcon.TrayMouseMove += (_, _) =>
        {
            _trayInfoPopup.IsOpen = true;
            _timerHiddenTrayInfoPopup.Start();
        };

        LanguageManager.Instance.LanguageChanged += (_, _) => UpdateTrayMenuHeaders();
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
        _trayIcon?.Visibility = Visibility.Hidden;
    }

    private void ShowMainWindowAndCreate()
    {
        ShowMainWindow();
        _mainViewModel?.CreateDiskCommand.Execute(null);
    }

    private void UpdateTrayMenuHeaders()
    {
        if (_trayIcon?.ContextMenu is not { } menu)
        {
            return;
        }

        ((MenuItem)menu.Items[0]!).Header = Loc.Get("Tray.Show");
        ((MenuItem)menu.Items[1]!).Header = Loc.Get("Tray.NewDisk");
        ((MenuItem)menu.Items[3]!).Header = Loc.Get("Tray.Exit");
    }
}