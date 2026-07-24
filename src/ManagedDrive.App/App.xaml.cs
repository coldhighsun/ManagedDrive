using ManagedDrive.Cli.Core;
using System.Windows.Interop;

namespace ManagedDrive.App;

/// <summary>
/// Application entry point. Owns the <see cref="MountManager"/> lifetime, initialises the
/// system tray icon, auto-mounts persisted disk profiles, and saves settings on exit. Tray icon,
/// tooltip, disk notifications, TEMP compatibility, session-ending save, and the WinFsp
/// prerequisite check are each delegated to a dedicated service in <c>Services/</c> — this class
/// is left owning startup/shutdown orchestration and window navigation.
/// </summary>
public partial class App
{
    private const string SingleInstanceMutexName = "Global\\ManagedDrive-4A7C2E1B-9F3D-4B8A-A1C5-3E6D2F0B8C9A";

    private CliPipeServer? _cliPipeServer;
    private DiskNotificationService? _diskNotificationService;
    private bool _isExiting;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;

    /// <summary>
    /// Cached handle of the main window, captured on the UI thread at startup. Used by
    /// <see cref="SessionEndingSaveHandler"/> (which runs on the <see cref="SystemEvents"/> thread,
    /// not the UI thread) to register a shutdown block reason without touching WPF objects
    /// cross-thread.
    /// </summary>
    private IntPtr _mainWindowHandle;

    private MountManager? _mountManager;
    private SessionEndingSaveHandler? _sessionEndingSaveHandler;
    private SettingsStore? _settings;
    private Mutex? _singleInstanceMutex;
    private TempDirCompatChecker? _tempDirCompatChecker;
    private TrayIconController? _trayIconController;
    private TrayTooltipController? _trayTooltipController;

    private void App_Exit(object sender, ExitEventArgs e)
    {
        if (_sessionEndingSaveHandler != null)
        {
            SystemEvents.SessionEnding -= _sessionEndingSaveHandler.OnSessionEnding;
        }
        _mainViewModel?.SaveSettings();
        _trayIconController?.Dispose();
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
        _sessionEndingSaveHandler = new(_mountManager, () => _mainWindowHandle);
        SystemEvents.SessionEnding += _sessionEndingSaveHandler.OnSessionEnding;
        _mainViewModel = new(_mountManager, _settings, config);
        _mainViewModel.ExitRequested += async (_, _) => await ShutdownAsync();
        _mainWindow = new(_mainViewModel);
        _mainWindow.Closing += MainWindow_Closing;
        _mainWindow.IsVisibleChanged += OnMainWindowVisibleChanged;

        // Force the HWND to exist now (on the UI thread) so SessionEndingSaveHandler can reference
        // it from the SystemEvents thread even when the window stays hidden in the tray.
        _mainWindowHandle = new WindowInteropHelper(_mainWindow).EnsureHandle();

        var iconStream = GetResourceStream(new("pack://application:,,,/ManagedDrive.ico"))!.Stream;
        _trayIconController = new(
            Dispatcher, iconStream, ShowMainWindow, ShowMainWindowAndCreate, ResetTempDirsFromTrayAsync,
            ShowMainWindowAndSettings, ShowAboutDialog, ExitApplication);
        _trayTooltipController = new(_mainViewModel, _trayIconController);
        _tempDirCompatChecker = new(_settings, _trayIconController, () => _mainWindow is { IsLoaded: true } ? _mainWindow : null);
        _mountManager.ActivityDetected += _trayIconController.OnActivityDetected;
        _diskNotificationService = new(_mainViewModel, _trayIconController, () => _mainWindow!.IsVisible);
        _tempDirCompatChecker.CheckOnStartup(config);

        if (config.StartMinimized)
        {
            _trayIconController.Visible = true;
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

    private async Task AutoMountDisksAsync()
    {
        if (_settings == null || _mainViewModel == null)
        {
            return;
        }

        // Mounted one at a time (not Task.WhenAll) so that password prompts for encrypted disks
        // appear sequentially rather than all at once.
        foreach (var profile in _settings.Load().Disks.Where(p => p.AutoMount))
        {
            await _mainViewModel.MountFromProfileAsync(profile);
        }
    }

    private void CheckWinFspPrerequisite()
    {
        if (WinFspPrerequisite.IsInstalled())
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
            Process.Start(new ProcessStartInfo("https://github.com/winfsp/winfsp/releases/tag/v2.2B3") { UseShellExecute = true });
        }

        Shutdown();
    }

    private async void ExitApplication()
    {
        var tempOnRamDisk = _mainViewModel != null && TempDirCompatChecker.IsTempOnAnyDisk(_mainViewModel.Disks);

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

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        _mainWindow!.Hide();
        _trayIconController!.Visible = true;
        _trayIconController.ShowBalloonTip("ManagedDrive", Loc.Get("Msg.StartedMinimized"), System.Windows.Forms.ToolTipIcon.Info);
    }

    /// <summary>
    /// Fires whenever the main window is hidden (minimized to tray) or shown again. Toggles each
    /// disk's <see cref="DiskViewModel.SetActivityTrackingEnabled"/> to match, since nothing is
    /// bound to the status bar while the window is hidden.
    /// </summary>
    private void OnMainWindowVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_mainViewModel == null)
        {
            return;
        }

        var isVisible = _mainWindow!.IsVisible;
        foreach (var vm in _mainViewModel.Disks)
        {
            vm.SetActivityTrackingEnabled(isVisible);
        }
    }

    private Task ResetTempDirsFromTrayAsync() => _tempDirCompatChecker!.ResetFromTrayAsync();

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
        _trayIconController?.Visible = false;
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
        if (_sessionEndingSaveHandler != null)
        {
            SystemEvents.SessionEnding -= _sessionEndingSaveHandler.OnSessionEnding;
        }
        _isExiting = true;

        if (_mainViewModel != null)
        {
            _mainViewModel.IsExiting = true;
            ShowMainWindow();
        }

        _cliPipeServer?.Dispose();
        _mainViewModel?.SaveSettings();
        _trayIconController?.Dispose();
        _mainViewModel?.Dispose();

        if (_mountManager != null && _trayIconController != null)
        {
            _mountManager.ActivityDetected -= _trayIconController.OnActivityDetected;
        }

        await Task.Run(() => _mountManager?.Dispose());
        Shutdown();
    }
}
