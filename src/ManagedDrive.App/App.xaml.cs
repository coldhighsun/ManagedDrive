using System.Globalization;
using H.NotifyIcon;
using ManagedDrive.App.Localization;
using ManagedDrive.App.Models;
using ManagedDrive.App.Services;
using ManagedDrive.App.ViewModels;
using ManagedDrive.Core;
using Serilog;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace ManagedDrive.App;

/// <summary>
/// Application entry point. Owns the <see cref="MountManager"/> lifetime, initialises the
/// system tray icon, auto-mounts persisted disk profiles, and saves settings on exit.
/// </summary>
public partial class App
{
    private const string SingleInstanceMutexName = "Global\\ManagedDrive-4A7C2E1B-9F3D-4B8A-A1C5-3E6D2F0B8C9A";

    private bool _isExiting;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private MountManager? _mountManager;
    private SettingsStore? _settings;
    private Mutex? _singleInstanceMutex;
    private TaskbarIcon? _trayIcon;

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

        _mountManager = new MountManager();
        _settings = new SettingsStore();
        LanguageManager.Instance.ApplyDefault(ResolveLanguage(_settings.Load().Language));
        _mainViewModel = new MainViewModel(_mountManager, _settings);
        _mainWindow = new MainWindow(_mainViewModel);
        _mainWindow.Closing += MainWindow_Closing;

        SetupTrayIcon();
        AutoMountDisks();

        _mainWindow.Show();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
            return;

        e.Cancel = true;
        _mainWindow!.Hide();
        if (_trayIcon != null)
            _trayIcon.Visibility = Visibility.Visible;
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

    private void ExitApplication()
    {
        _isExiting = true;
        SaveSettings();
        _trayIcon?.Dispose();
        _mainViewModel?.Dispose();
        _mountManager?.Dispose();
        Shutdown();
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
            Language = LanguageManager.Instance.CurrentLanguage,
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
            ToolTipText = "ManagedDrive",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/ManagedDrive.ico")),
            ContextMenu = menu,
            Visibility = Visibility.Hidden,
        };
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
        _trayIcon.ForceCreate();

        LanguageManager.Instance.LanguageChanged += (_, _) => UpdateTrayMenuHeaders();
    }

    private static string ResolveLanguage(string? saved)
    {
        if (!string.IsNullOrEmpty(saved))
            return saved;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en-US";
    }

    private void UpdateTrayMenuHeaders()
    {
        if (_trayIcon?.ContextMenu is not { } menu)
            return;
        ((MenuItem)menu.Items[0]!).Header = Loc.Get("Tray.Show");
        ((MenuItem)menu.Items[1]!).Header = Loc.Get("Tray.NewDisk");
        ((MenuItem)menu.Items[3]!).Header = Loc.Get("Tray.Exit");
    }

    private void ShowMainWindow()
    {
        _mainWindow?.Show();
        _mainWindow?.Activate();
        if (_trayIcon != null)
            _trayIcon.Visibility = Visibility.Hidden;
    }

    private void ShowMainWindowAndCreate()
    {
        ShowMainWindow();
        _mainViewModel?.CreateDiskCommand.Execute(null);
    }
}
