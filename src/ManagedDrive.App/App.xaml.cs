using ManagedDrive.Cli.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
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
    private static readonly TimeSpan ExitDisposeTimeout = TimeSpan.FromSeconds(20);

    private CliPipeServer? _cliPipeServer;
    private DiskNotificationService? _diskNotificationService;
    private GlobalMountCoordinator? _globalMountCoordinator;
    private bool _isExiting;
    private ILogger<App> _logger = NullLoggerFactory.Instance.CreateLogger<App>();
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;

    /// <summary>
    /// Cached handle of the main window, captured on the UI thread at startup. Used by
    /// <see cref="SessionEndingSaveHandler"/> (which runs on the <see cref="SystemEvents"/> thread,
    /// not the UI thread) to register a shutdown block reason without touching WPF objects
    /// cross-thread.
    /// </summary>
    private IntPtr _mainWindowHandle;

    private bool _minimizedToTrayBalloonShown;
    private MountManager? _mountManager;

    /// <summary>
    /// DI container root, built by <see cref="ConfigureServices"/>. Currently only backs the
    /// logging infrastructure (<see cref="ILoggerFactory"/>/<see cref="ILogger{T}"/>) and
    /// <see cref="MainViewModel"/>'s injected logger — most of this class's other collaborators
    /// (tray/services) are still wired up manually because their constructors take runtime
    /// delegates (window-visibility callbacks, tray actions) that don't fit a container
    /// registration cleanly. Disposing this also disposes the registered <see cref="ILoggerFactory"/>.
    /// </summary>
    private ServiceProvider? _serviceProvider;

    private SessionEndingSaveHandler? _sessionEndingSaveHandler;
    private SettingsStore? _settings;
    private Mutex? _singleInstanceMutex;
    private TempDirCompatChecker? _tempDirCompatChecker;
    private TrayIconController? _trayIconController;
    private TrayTooltipController? _trayTooltipController;
    private UpdateCheckService? _updateCheckService;

    private void App_Exit(object sender, ExitEventArgs e)
    {
        _logger.LogInformation("App_Exit invoked.");

        TeardownBeforeMountManagerDispose();

        // Safety net: if ShutdownAsync already disposed the mount manager, this is a no-op.
        // Bounded so a stuck final save can't hang process exit indefinitely.
        if (!Task.Run(() => _mountManager?.Dispose()).Wait(ExitDisposeTimeout))
        {
            _logger.LogWarning("MountManager.Dispose did not complete within the exit timeout of {Timeout}", ExitDisposeTimeout);
        }
        if (_singleInstanceMutex != null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }

        Log.CloseAndFlush();
        _serviceProvider?.Dispose();
    }

    private async void App_Startup(object sender, StartupEventArgs e)
    {
        ConfigureServices();
        RegisterGlobalExceptionHandlers();

        _settings = new();
        var config = _settings.Load();
        LanguageManager.Instance.ApplyDefault(config.Language);
        ThemeManager.Instance.ApplyDefault(config.Theme);

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
                Loc.Get("Msg.AlreadyRunning"),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        CheckWinFspPrerequisite();

        _mountManager = new();
        _sessionEndingSaveHandler = new(
            _mountManager,
            () => _mainWindowHandle,
            _serviceProvider!.GetRequiredService<ILogger<SessionEndingSaveHandler>>());
        SystemEvents.SessionEnding += _sessionEndingSaveHandler.OnSessionEnding;
        _mainViewModel = new(_mountManager, _settings, config, _serviceProvider!.GetRequiredService<ILogger<MainViewModel>>());
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
        _diskNotificationService = new(
            _mainViewModel, _trayIconController, () => _mainWindow!.IsVisible,
            _serviceProvider!.GetRequiredService<ILogger<DiskNotificationService>>());

        // Constructed before AutoMountDisksAsync so that an auto-mounted disk which is already the
        // TEMP target gets its global symlink published at startup. Rooted as a field only to keep
        // its Disks.CollectionChanged subscription alive.
        _globalMountCoordinator = new(_mainViewModel);

        _tempDirCompatChecker.CheckOnStartup(config);

        _updateCheckService = new(_settings, _trayIconController, () => _mainWindow is { IsLoaded: true } ? _mainWindow : null);
        _mainViewModel.UpdateCheckService = _updateCheckService;
        _ = _updateCheckService.CheckOnStartupAsync(config);

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
        _tempDirCompatChecker.CheckAfterAutoMount(config, _mainViewModel.Disks);

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

        var profiles = _settings.Load().Disks.Where(p => p.AutoMount).ToList();
        if (profiles.Count == 0)
        {
            return;
        }

        // Mounted one at a time (not Task.WhenAll) so that password prompts for encrypted disks
        // appear sequentially rather than all at once.
        try
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];

                // Only a saved image path (not a source archive) has a byte size known up front;
                // archive-imported profiles fall back to the indeterminate/no-detail-text case.
                var totalBytes = profile.PersistImagePath != null && File.Exists(profile.PersistImagePath)
                    ? (ulong)new FileInfo(profile.PersistImagePath).Length
                    : (ulong?)null;

                // StatusText's setter is private, so re-call Start() each iteration to update the
                // text; that also resets Progress/DetailText, which the progress callback below
                // then re-populates as this disk's own load proceeds.
                _mainViewModel.BusyOverlay.Start(Loc.Format("Busy.LoadingDisks", i + 1, profiles.Count), totalBytes: totalBytes);
                var progress = new Progress<double>(_mainViewModel.BusyOverlay.Report);
                await _mainViewModel.MountFromProfileAsync(profile, progress);
            }
        }
        finally
        {
            _mainViewModel.BusyOverlay.Stop();
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
            Process.Start(new ProcessStartInfo("https://github.com/winfsp/winfsp/releases/tag/v2.2B4") { UseShellExecute = true });
        }

        Shutdown();
    }

    /// <summary>
    /// Builds the DI container: registers Serilog-backed logging (<see cref="ILoggerFactory"/>/
    /// <see cref="ILogger{T}"/>) and wires the resulting factory through <see cref="AppLog"/> so
    /// <c>Core</c> types get a real logger too (<c>SnapshotManager</c> is a static class and
    /// <c>RamDisk</c> is constructed via a static factory, so neither can take a
    /// constructor-injected logger without breaking their public API — <see cref="AppLog"/> is
    /// the documented bridge for those two, still ultimately backed by this container).
    /// Must run before <see cref="RegisterGlobalExceptionHandlers"/> so unhandled exceptions
    /// from that point on are captured.
    /// </summary>
    private void ConfigureServices()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ManagedDrive", "logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Async(sinkConfig => sinkConfig.File(
                Path.Combine(logDirectory, "log-.txt"),
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 5))
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog(dispose: true));
        _serviceProvider = services.BuildServiceProvider();

        AppLog.Configure(_serviceProvider.GetRequiredService<ILoggerFactory>());
        _logger = _serviceProvider.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("ManagedDrive started (version {Version}).", UpdateCheckService.GetRunningVersion());
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

        if (!_minimizedToTrayBalloonShown)
        {
            _minimizedToTrayBalloonShown = true;
            _trayIconController.ShowBalloonTip("ManagedDrive", Loc.Get("Msg.StartedMinimized"), System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Last-resort fallback for non-UI-thread fatal exceptions; the process cannot be kept
        // alive at this point, so this only guarantees the failure is on disk before it dies.
        _logger.LogCritical(e.ExceptionObject as Exception, "Fatal unhandled exception outside the UI thread");
        Log.CloseAndFlush();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unhandled exception on the UI thread");
        MessageBox.Show(
            Loc.Format("Msg.UnexpectedErrorBody", e.Exception.Message),
            Loc.Get("Msg.UnexpectedErrorTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
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

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger.LogWarning(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    /// <summary>
    /// Registers process-wide exception handlers so an unhandled exception on the UI thread
    /// (including one thrown by an <c>async void</c> command that resumes on the WPF dispatcher
    /// after an <c>await</c>) is logged and shown to the user instead of crashing the process.
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private Task ResetTempDirsFromTrayAsync() => _tempDirCompatChecker!.ResetFromTrayAsync();

    private void ShowAboutDialog()
    {
        var dialog = new AboutDialog(_updateCheckService);
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
        _logger.LogInformation("ShutdownAsync starting.");

        _isExiting = true;

        if (_mainViewModel != null)
        {
            _mainViewModel.IsExiting = true;
            ShowMainWindow();
        }

        TeardownBeforeMountManagerDispose();

        await Task.Run(() => _mountManager?.Dispose((disk, diskFraction, overallFraction, totalBytes) =>
            Application.Current.Dispatcher.BeginInvoke(() =>
                _mainViewModel?.ReportExitSaveProgress(disk.Options.MountPoint, overallFraction, diskFraction, totalBytes))));

        _logger.LogInformation("ShutdownAsync completed; shutting down application.");
        Shutdown();
    }

    /// <summary>
    /// Common teardown shared by <see cref="App_Exit"/> and <see cref="ShutdownAsync"/>, run
    /// before either one disposes <see cref="_mountManager"/> (which they do differently: a
    /// bounded-wait safety net vs. an awaited call with exit-save progress reporting).
    /// </summary>
    private void TeardownBeforeMountManagerDispose()
    {
        if (_sessionEndingSaveHandler != null)
        {
            SystemEvents.SessionEnding -= _sessionEndingSaveHandler.OnSessionEnding;
        }
        if (_mountManager != null && _trayIconController != null)
        {
            _mountManager.ActivityDetected -= _trayIconController.OnActivityDetected;
        }
        _cliPipeServer?.Dispose();
        _mainViewModel?.SaveSettings();
        _trayIconController?.Dispose();
        _mainViewModel?.Dispose();
    }
}