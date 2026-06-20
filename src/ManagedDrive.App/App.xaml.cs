using ManagedDrive.App.Models;
using ManagedDrive.App.Services;
using ManagedDrive.App.ViewModels;
using ManagedDrive.Core;
using Serilog;
using System.IO;
using System.Windows;

namespace ManagedDrive.App;

/// <summary>
/// Application entry point. Owns the <see cref="MountManager"/> lifetime, initialises the
/// system tray icon, auto-mounts persisted disk profiles, and saves settings on exit.
/// </summary>
public partial class App
{
    private const string SingleInstanceMutexName = "Global\\ManagedDrive-4A7C2E1B-9F3D-4B8A-A1C5-3E6D2F0B8C9A";

    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private MountManager? _mountManager;
    private SettingsStore? _settings;
    private Mutex? _singleInstanceMutex;
    private NotifyIcon? _trayIcon;

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
            System.Windows.MessageBox.Show(
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
                Path.Combine(System.Windows.Forms.Application.StartupPath, "logs", "log-.txt"),
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
        _mainViewModel = new MainViewModel(_mountManager, _settings);
        _mainWindow = new MainWindow(_mainViewModel);

        SetupTrayIcon();
        AutoMountDisks();

        _mainWindow.Show();
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
        _mainWindow!.Closing -= null;
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
            Disks = _mainViewModel.GetProfiles().ToList(),
        });
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Text = "ManagedDrive",
            Icon = SystemIcons.Information,
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowMainWindow());
        menu.Items.Add("New Disk", null, (_, _) => ShowMainWindowAndCreate());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        _mainWindow?.Show();
        _mainWindow?.Activate();
    }

    private void ShowMainWindowAndCreate()
    {
        ShowMainWindow();
        _mainViewModel?.CreateDiskCommand.Execute(null);
    }
}