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
    private static readonly TimeSpan ExitDisposeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a second instance launched with a command keeps trying to hand it to the running
    /// instance, which may still be starting up (its CLI pipe only opens once its window is up).
    /// </summary>
    private static readonly TimeSpan HandOffTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay between a second instance's attempts to hand its command to the running instance.
    /// </summary>
    private static readonly TimeSpan HandOffRetryInterval = TimeSpan.FromMilliseconds(250);

    private CliPipeServer? _cliPipeServer;
    private DiskNotificationService? _diskNotificationService;
    private GlobalMountCoordinator? _globalMountCoordinator;
    private bool _isExiting;

    /// <summary>
    /// Set to 1 by the first <see cref="ShutdownAsync"/> call so a repeated exit request is ignored.
    /// </summary>
    private int _shutdownStarted;
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

    /// <summary>
    /// Sequences the end of the startup splash (minimum display time, close, main window), and
    /// tells waiters when that is done. Created at startup once the settings are known.
    /// </summary>
    private SplashLifecycle? _splashLifecycle;

    /// <summary>
    /// Measures how long the splash has been up, to honour <see cref="SplashPolicy.MinimumDisplay"/>.
    /// </summary>
    private readonly Stopwatch _splashStopwatch = new();

    /// <summary>
    /// The startup splash, or <see langword="null"/> when none was shown or it is already closed.
    /// While it is set the main window stays hidden and the loading progress goes to the splash.
    /// </summary>
    private SplashWindow? _splashWindow;

    private TempDirCompatChecker? _tempDirCompatChecker;
    private EnvRestoreTrayAction? _envRestoreTrayAction;
    private TrayIconController? _trayIconController;
    private TrayTooltipController? _trayTooltipController;
    private UpdateCheckService? _updateCheckService;

    private void App_Exit(object sender, ExitEventArgs e)
    {
        _logger.LogInformation("App_Exit invoked.");

        TeardownBeforeMountManagerDispose();
        RunTeardownStep(() => TempDirResetService.WaitForPendingBroadcasts(TimeSpan.FromSeconds(3)));

        // Safety net: if ShutdownAsync already disposed the mount manager, this is a no-op.
        // Bounded so a stuck final save can't hang process exit indefinitely.
        try
        {
            if (!Task.Run(() => _mountManager?.Dispose()).Wait(ExitDisposeTimeout))
            {
                _logger.LogWarning("MountManager.Dispose did not complete within the exit timeout of {Timeout}", ExitDisposeTimeout);
            }
        }
        catch (Exception ex)
        {
            // MountManager.Dispose rethrows after every disk was attempted; the mutex release and
            // log flush below must still run.
            _logger.LogError(ex, "Disposing the mounted disks failed during exit.");
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

        AppConfiguration config;
        try
        {
            _settings = new();
            config = _settings.Load();
            LanguageManager.Instance.ApplyDefault(config.Language);
            ThemeManager.Instance.ApplyDefault(config.Theme);

            if (!AppInstance.TryAcquire(out _singleInstanceMutex))
            {
                await HandOffToRunningInstanceAsync(e.Args);
                Shutdown();
                return;
            }

            // Before the splash, so its warning box is not covered by it. Shutdown() only queues the
            // exit; return so nothing below runs — in particular no MainViewModel gets created, so
            // App_Exit has no (empty) disk list to save over the user's settings.
            if (!CheckWinFspPrerequisite())
            {
                Shutdown();
                return;
            }

            _splashLifecycle = new(
                config.StartMinimized, () => _splashStopwatch.Elapsed, Task.Delay, ShowMainWindowAtStartup);
            await ShowSplashAsync(config.StartMinimized);

            await StartUiAsync(_settings, config);
        }
        catch (Exception ex)
        {
            _splashWindow?.Dismiss();
            _splashWindow = null;

            // Nothing is on screen yet, so the dispatcher handler's "log and keep running" would
            // leave a windowless process behind — one still holding the single-instance mutex, so
            // every later launch would just report "already running".
            _logger.LogCritical(ex, "Startup failed");
            MessageBox.Show(
                Loc.Format("Msg.StartupFailedBody", ex.Message),
                Loc.Get("Msg.UnexpectedErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Listening before auto-mount so a command sent meanwhile (e.g. from the Explorer context
        // menu) is queued rather than refused, but not run until auto-mount has finished.
        var autoMountDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cliPipeServer = new(_mainViewModel!, autoMountDone.Task);
        _cliPipeServer.Start();

        try
        {
            await AutoMountDisksAsync();
        }
        finally
        {
            // Queued CLI commands do not depend on the splash or the main window, so they are
            // released first; the splash is then closed even when auto-mount threw, so that it
            // never outlives the loading.
            await StartupCompletion.ReleaseThenCloseSplashAsync(autoMountDone, CloseSplashAndShowMainWindowAsync);
        }

        _tempDirCompatChecker!.CheckAfterAutoMount(config, _mainViewModel!.Disks);

        if (e.Args.Length > 0)
        {
            // Launched with CLI-style args (e.g. from the Explorer context menu) as the first
            // instance: execute the command directly against this instance's MainViewModel.
            var controller = new MainViewModelCliDiskController(_mainViewModel);
            var result = await _cliPipeServer.RunExclusiveAsync(() =>
                CliCommandProcessor.ExecuteAsync(e.Args, controller, Environment.CurrentDirectory));
            if (result.ExitCode != 0)
            {
                MessageBox.Show(result.Message, "ManagedDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>
    /// Shows the startup splash and lets it render. Shown also when the app starts minimized; only
    /// the main window stays hidden then, and the splash gets no taskbar button.
    /// </summary>
    /// <param name="startMinimized">Whether the app starts minimized to the tray.</param>
    private async Task ShowSplashAsync(bool startMinimized)
    {
        // The splash is the only window until the main window is shown (never, when starting
        // minimized); closing it must not count as the last window closing and end the app.
        // CloseSplashAndShowMainWindowAsync restores the default once it is gone.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var splash = new SplashWindow(showInTaskbar: !startMinimized);
        _splashWindow = splash;
        splash.Show();
        _splashStopwatch.Restart();
        _splashLifecycle!.Show(() =>
        {
            try
            {
                splash.Dismiss();
            }
            finally
            {
                // Always restored: left on OnExplicitShutdown, closing the last window would no
                // longer end the app and a windowless instance would keep the single-instance mutex.
                ShutdownMode = ShutdownMode.OnLastWindowClose;
            }
        });

        // StartUiAsync keeps the UI thread busy for a while, in chunks. The splash starts its
        // animations in its Loaded handler, which runs at a lower priority than Render, so yielding
        // only to Render let the startup work run first and the animations began after it. Waiting
        // for ContextIdle lets the splash lay out, start its animations and render a first frame;
        // WPF then keeps running those (transform animations) on the render thread meanwhile.
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// Closes the startup splash, if still up, after it has been shown for its minimum time, and
    /// shows the main window in its place unless the app starts minimized. Without a splash it only
    /// completes <see cref="SplashLifecycle.Closed"/>, which the startup update prompt waits for, so
    /// it must run on every startup path.
    /// </summary>
    private async Task CloseSplashAndShowMainWindowAsync()
    {
        // Cleared first so the loading progress switches to the busy overlay at once, even while
        // the lifecycle is still waiting out the minimum display time.
        _splashWindow = null;
        await _splashLifecycle!.CloseAsync();
    }

    /// <summary>
    /// Brings the main window to the front for the first time after startup.
    /// </summary>
    private void ShowMainWindowAtStartup()
    {
        // Not once the app is exiting: a command such as `mdrive exit` can be released before the
        // splash has finished its minimum display time, and showing a window then is at best noise
        // and can throw while the application shuts down.
        if (_mainWindow == null || _isExiting)
        {
            return;
        }

        _mainWindow.Topmost = true;
        _mainWindow.Show();
        _mainWindow.Activate();
        _mainWindow.Topmost = false;
    }

    /// <summary>
    /// Hands this second instance's command line to the instance already running, or tells the
    /// user it is already running if there is no command to hand over.
    /// </summary>
    /// <param name="args">The process's command-line arguments.</param>
    private static async Task HandOffToRunningInstanceAsync(string[] args)
    {
        if (args.Length == 0)
        {
            MessageBox.Show(
                Loc.Get("Msg.AlreadyRunning"),
                "ManagedDrive",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Launched with CLI-style args (e.g. from the Explorer context menu) while another instance
        // is running: forward the command to it instead of showing the "already running" dialog.
        // Retried because that instance may still be starting up and not listening yet.
        var response = await CliPipeClient.SendWithRetryAsync(args, HandOffTimeout, HandOffRetryInterval, AppInstance.IsRunning);
        if (response == null)
        {
            MessageBox.Show(Loc.Get("Msg.CommandNotDelivered"), "ManagedDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (response.ExitCode != 0)
        {
            MessageBox.Show(response.Message, "ManagedDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Lets the dispatcher process everything queued at higher priorities (rendering, animation
    /// frames, input) before startup carries on.
    /// </summary>
    /// <returns>A task that completes once the dispatcher is free again.</returns>
    private async Task YieldToUiAsync() => await Dispatcher.Yield(DispatcherPriority.Background);

    /// <summary>
    /// Runs the first instance's UI startup: builds the view model, window and tray services, and
    /// shows the window or tray icon. Done in chunks with a yield to the dispatcher in between, so
    /// that the startup splash keeps rendering instead of freezing for the whole of it.
    /// </summary>
    /// <param name="settings">The app's settings store.</param>
    /// <param name="config">The settings loaded at startup.</param>
    /// <returns>A task that completes once the UI is up.</returns>
    private async Task StartUiAsync(SettingsStore settings, AppConfiguration config)
    {
        _mountManager = new();
        _mainViewModel = new(_mountManager, settings, _serviceProvider!.GetRequiredService<ILogger<MainViewModel>>(), config.Disks);
        _mainViewModel.ExitRequested += async (_, _) => await ShutdownAsync();
        await YieldToUiAsync();

        _mainWindow = new(_mainViewModel);

        // The splash, created first, would otherwise stay Application.MainWindow, which password
        // prompts and dialogs use as their owner.
        MainWindow = _mainWindow;
        _mainWindow.Closing += MainWindow_Closing;
        _mainWindow.IsVisibleChanged += OnMainWindowVisibleChanged;
        _mainWindow.StateChanged += (_, _) => UpdateForMainWindowShownState();

        // Force the HWND to exist now (on the UI thread) so SessionEndingSaveHandler can reference
        // it from the SystemEvents thread even when the window stays hidden in the tray.
        _mainWindowHandle = new WindowInteropHelper(_mainWindow).EnsureHandle();

        // Subscribed only now, with the handle in place and no yield in between: the handler needs
        // it to register a shutdown block reason, and a session end during an earlier yield would
        // find none.
        _sessionEndingSaveHandler = new(
            _mountManager,
            () => _mainWindowHandle,
            _serviceProvider!.GetRequiredService<ILogger<SessionEndingSaveHandler>>(),
            ResetEnvironmentForSessionEnding);
        SystemEvents.SessionEnding += _sessionEndingSaveHandler.OnSessionEnding;
        await YieldToUiAsync();

        var iconStream = GetResourceStream(new("pack://application:,,,/ManagedDrive.ico"))!.Stream;
        _trayIconController = new(
            Dispatcher, iconStream, _mainViewModel, ShowMainWindow, ShowMainWindowAndCreate, RestoreEnvFromTrayAsync,
            ShowMainWindowAndSettings, ShowAboutDialog, ExitApplication);
        _trayTooltipController = new(_mainViewModel, _trayIconController);
        _tempDirCompatChecker = new(settings);
        _envRestoreTrayAction = new(
            _trayIconController, () => _mainWindow is { IsVisible: true, WindowState: not WindowState.Minimized } ? _mainWindow : null,
            group => _mainViewModel!.RestoreEnvAsync(group));
        _mountManager.ActivityDetected += _trayIconController.OnActivityDetected;
        _diskNotificationService = new(
            _mainViewModel, _trayIconController, () => WindowVisibility.IsShownToUser(_mainWindow),
            _serviceProvider!.GetRequiredService<ILogger<DiskNotificationService>>());
        await YieldToUiAsync();

        // Constructed before AutoMountDisksAsync so that an auto-mounted disk which is already the
        // TEMP target gets its global symlink published at startup. Rooted as a field only to keep
        // its Disks.CollectionChanged subscription alive.
        _globalMountCoordinator = new(_mainViewModel, _serviceProvider!.GetRequiredService<ILogger<GlobalMountCoordinator>>());

        // Nothing is mounted yet, so every environment variable still pointing into a RAM disk is a
        // leftover of a crash. Restored now rather than after auto-mount, which can take a while
        // (large or encrypted images); disks that redirect variables set them again once mounted.
        // Before the TEMP check, which resets what is left to the Windows defaults.
        _mainViewModel.RestoreDanglingEnvRedirects();

        // The check may show message boxes. The splash is Topmost and would cover them, so it steps
        // back for the duration; boxes opened meanwhile land above it.
        var coveringSplash = _splashWindow;
        coveringSplash?.PauseTopmost(true);

        try
        {
            _tempDirCompatChecker.CheckOnStartup(config);
        }
        finally
        {
            coveringSplash?.PauseTopmost(false);
        }

        await YieldToUiAsync();

        _updateCheckService = new(settings, _trayIconController, () => _mainViewModel.Disks);
        _mainViewModel.UpdateCheckService = _updateCheckService;
        _ = PromptForUpdateOnStartupAsync(config);

        // The main window is not shown here: the splash is always up at this point, and
        // SplashLifecycle shows the main window once the loading has finished (never, when
        // starting minimized, where only the tray icon appears).
        if (config.StartMinimized)
        {
            _trayIconController.Visible = true;
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

        var viewModel = _mainViewModel;
        var overlay = viewModel.BusyOverlay;

        // A disk loaded from a saved image has that image's size; a disk without one (empty, or
        // its image is missing) loads at once and counts as 0 bytes. Only a source archive has
        // no size known up front: it is unknown (null) and takes an average share of the bar.
        // Read off the UI thread: the images may sit on a network share or a sleeping disk.
        var sizes = await Task.Run(() => profiles
            .Select(p => p.SourceArchivePath is null ? FileSizeProbe.TryGetSize(p.PersistImagePath) ?? 0UL : (ulong?)null)
            .ToList());

        // The byte detail under the bar is shown unless an archive disk makes the total unknown
        // (its average share in the bar's fraction wouldn't match any byte count).
        var knownBytes = sizes.Aggregate(0UL, (sum, size) => sum + (size ?? 0UL));
        ulong? totalBytes = sizes.All(s => s.HasValue) && knownBytes > 0 ? knownBytes : null;

        // Created on the UI thread, so the callbacks of the loads running on the thread pool are
        // marshalled back to it before they touch the overlay. A 0-byte disk gets a token weight
        // (a weight of 0 would mean "unknown" and give it an average share).
        // While the startup splash is up it shows the progress; once it is gone (a password prompt
        // needs the main window) the busy overlay takes over.
        // Coalesced and delivered below input priority: Progress<T> posts every report at Normal
        // priority, which outranks mouse input, so a burst of reports delayed clicks on the splash.
        // Completed in the finally below: that shows the final value and drops any delivery still
        // queued, so it cannot set the progress of an overlay operation started after it.
        Action<Action> postBackground = action => Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
        var overlayProgress = new CoalescingProgress(fraction =>
        {
            if (_splashWindow is { } splash)
            {
                splash.Report(fraction);
            }
            else
            {
                overlay.Report(fraction);
            }
        }, postBackground);
        var aggregate = new AggregateProgress(
            sizes.Select(s => s is { } size ? Math.Max(size, 1UL) : 0.0).ToList(),
            overlayProgress.Report);

        viewModel.BeginAutoMount(profiles.Count);
        var firstStatus = Loc.Format("Busy.LoadingDisks", 1, profiles.Count);
        if (_splashWindow is { } startupSplash)
        {
            startupSplash.SetStatus(firstStatus);
        }
        else
        {
            overlay.Start(firstStatus, totalBytes: totalBytes);
        }

        try
        {
            // Loading (reading, decrypting, decompressing, mounting) runs for up to
            // AutoMountConcurrency disks at once; everything that touches the UI — password
            // prompts, adding the disk, status text — is finished one disk at a time on this
            // thread, in profile order except that disks needing a password come last, so
            // prompts appear sequentially and don't hold back the disks already mounted.
            await AutoMountScheduler.RunAsync(
                profiles.Count,
                AutoMountConcurrency,
                index => viewModel.TryMountFromProfileAsync(profiles[index], aggregate.ForOperation(index)),
                attempt => attempt.NeedsPassword,
                async (index, attempt) =>
                {
                    try
                    {
                        if (attempt.NeedsPassword && _splashWindow != null)
                        {
                            // The password prompt must not sit behind (or under) the splash: hand
                            // over to the main window and its busy overlay first.
                            await CloseSplashAndShowMainWindowAsync();
                            overlay.Start(
                                Loc.Format("Busy.LoadingDisks", Math.Min(viewModel.AutoMountCompleted + 1, profiles.Count), profiles.Count),
                                totalBytes: totalBytes);

                            // Start() resets the bar; carry over what the splash had reached.
                            overlay.Report(aggregate.Combined);
                        }

                        await viewModel.CompleteMountFromProfileAsync(profiles[index], attempt, aggregate.ForOperation(index));
                    }
                    finally
                    {
                        // Counted even when finishing failed, so the bar and the tray line don't
                        // stay short of the total.
                        CountDiskDone(index);
                    }
                },
                (index, ex) => _logger.LogError(ex, "Finishing the auto-mount of {MountPoint} failed.", profiles[index].MountPoint),
                (index, ex) =>
                {
                    _logger.LogError(ex, "Loading {MountPoint} failed.", profiles[index].MountPoint);
                    viewModel.FailAutoMount(profiles[index], ex.Message);
                    CountDiskDone(index);
                });

            void CountDiskDone(int index)
            {
                aggregate.Complete(index);
                var finished = viewModel.ReportAutoMountDiskDone();
                var status = Loc.Format("Busy.LoadingDisks", Math.Min(finished + 1, profiles.Count), profiles.Count);
                if (_splashWindow is { } splash)
                {
                    splash.SetStatus(status);
                }
                else
                {
                    overlay.UpdateStatusText(status);
                }
            }
        }
        finally
        {
            // The progress callback runs inside Complete(); the cleanup must happen even if it throws.
            try
            {
                overlayProgress.Complete();
            }
            finally
            {
                overlay.Stop();
                viewModel.EndAutoMount();
            }
        }
    }

    /// <summary>
    /// How many saved disks are loaded at the same time at startup. Two overlaps one disk's file
    /// reading and decryption with another's decompression and drive-letter wait without making
    /// two large images fight for a mechanical drive; a single disk's Zstd decoding is already
    /// multi-threaded, so more would add little.
    /// </summary>
    private const int AutoMountConcurrency = 2;

    /// <summary>
    /// Verifies WinFsp is installed; if not, tells the user and offers to open its download page.
    /// </summary>
    /// <returns>
    /// <c>true</c> if WinFsp is installed and startup can continue; <c>false</c> if the app must exit.
    /// </returns>
    private static bool CheckWinFspPrerequisite()
    {
        if (WinFspPrerequisite.IsInstalled())
        {
            return true;
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

        return false;
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

        if (_mainViewModel is { Disks.Count: > 0 } or { HasPendingUnmounts: true })
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
                _mainViewModel!.RestoreUserTemp();
            }
        }

        await ShutdownAsync();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            // Keep the window (and so the app) alive while ShutdownAsync's exit save is still
            // running: letting Alt+F4 close it here would end the app via OnLastWindowClose, and
            // App_Exit's safety-net Dispose is a no-op once ShutdownAsync has taken the disk list,
            // so the in-flight save would be killed with the process. The final Shutdown() call
            // is unaffected — WPF ignores Closing cancellation during Application.Shutdown.
            e.Cancel = true;
            return;
        }

        if (_settings?.Load().CloseToTray == false)
        {
            e.Cancel = true;
            ExitApplication();
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
    /// Fires whenever the main window is hidden (minimized to tray) or shown again.
    /// </summary>
    private void OnMainWindowVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        UpdateForMainWindowShownState();

    /// <summary>
    /// Runs whenever the main window is hidden, shown, minimized or restored. Toggles each disk's
    /// <see cref="DiskViewModel.SetActivityTrackingEnabled"/> to match, since nobody sees the
    /// status bar while the window is hidden or minimized, and once the window is back in view
    /// hides a tray icon that was only shown for a balloon tip.
    /// </summary>
    private void UpdateForMainWindowShownState()
    {
        if (_mainViewModel == null)
        {
            return;
        }

        var isShown = WindowVisibility.IsShownToUser(_mainWindow);
        foreach (var vm in _mainViewModel.Disks)
        {
            vm.SetActivityTrackingEnabled(isShown);
        }

        if (isShown)
        {
            _trayIconController?.HideIfShownForBalloonOnly();
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

    private Task RestoreEnvFromTrayAsync(EnvRestoreGroup? group) =>
        _mainViewModel!.RejectEnvRestoreIfBusy() ? Task.CompletedTask : _envRestoreTrayAction!.RunAsync(group);

    /// <summary>
    /// Runs the startup update check and, when a newer release exists, asks the user whether to install it.
    /// A modal prompt is only shown while the main window is visible; when the app sits in the tray
    /// (e.g. started minimized) a balloon pointing at About is shown instead of interrupting the user.
    /// </summary>
    private async Task PromptForUpdateOnStartupAsync(AppConfiguration config)
    {
        var service = _updateCheckService!;
        var info = await service.CheckOnStartupAsync(config);
        if (info is null || _isExiting)
        {
            return;
        }

        // The main window is hidden while the splash is up; decide dialog versus balloon only once
        // it is known whether the main window is shown.
        await _splashLifecycle!.Closed;
        if (_isExiting)
        {
            return;
        }

        if (WindowVisibility.IsShownToUser(_mainWindow))
        {
            UpdateDialog.ShowFor(service, info, _mainWindow);
        }
        else
        {
            service.NotifyUpdateAvailable(info);
        }
    }

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
        // The window, and the dialogs opened from it, must not end up under the splash.
        _splashWindow?.ReleaseTopmost();
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

    /// <summary>
    /// Puts the user's TEMP directory and the preset environment variables back before Windows
    /// logs off or shuts down, which skips the normal exit path. Runs on the
    /// <see cref="SystemEvents"/> thread, so it only uses thread-safe state (the mount manager, the
    /// registry, the redirector) and never the <c>Disks</c> collection.
    /// No change is broadcast: the process may be killed any moment and nothing is left to notify, so
    /// only the registry writes, which are what the next sign-in reads, are done.
    /// </summary>
    private void ResetEnvironmentForSessionEnding()
    {
        // First, so TEMP gets back what it held before; the reset below only catches a TEMP set by
        // an older version, which nothing recorded.
        _mainViewModel?.RestoreAllEnvRedirects(broadcast: false);

        if (_mountManager is { } mountManager &&
            TempDirCompatChecker.IsTempOnAnyMountPoint(mountManager.GetAll().Select(disk => disk.Options.MountPoint)))
        {
            TempDirResetService.Reset(broadcast: false);
        }
    }

    private async Task ShutdownAsync()
    {
        // A second exit request (CLI `exit` twice, or CLI exit plus tray Exit) must not start a
        // concurrent teardown and MountManager.Dispose while the first is still saving.
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            _logger.LogInformation("ShutdownAsync ignored: a shutdown is already in progress.");
            return;
        }

        _logger.LogInformation("ShutdownAsync starting.");

        _isExiting = true;

        if (_mainViewModel != null)
        {
            _mainViewModel.IsExiting = true;
            ShowMainWindow();

            // Before the disks go away: the variables must not be left pointing at them.
            // Isolated: a failure here must not skip the exit save of the disks below.
            RunTeardownStep(() => _mainViewModel.RestoreAllEnvRedirects());
        }

        // The view models unsubscribe from each disk's SaveFailed while being disposed in the
        // teardown, before the final save runs, so failures are collected from the disks directly.
        var saveFailures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var observedDisks = (_mountManager?.GetAll() ?? [])
            .Concat(_mainViewModel?.PendingUnmountDisks ?? [])
            .ToList();
        foreach (var disk in observedDisks)
        {
            var mountPoint = disk.Options.MountPoint;
            disk.SaveFailed += (_, ex) => saveFailures.Enqueue(Loc.Format("Status.SaveFailed", mountPoint, ex.Message));
        }

        TeardownBeforeMountManagerDispose();

        // A disk unmounted just before exit is already gone from the mount list, so the dispose
        // below would not wait for its final save and the process would kill it mid-write.
        if (_mainViewModel != null)
        {
            await _mainViewModel.WaitForPendingUnmountsAsync();
        }

        try
        {
            await Task.Run(() => _mountManager?.Dispose((disk, diskFraction, overallFraction, totalBytes) =>
                Current.Dispatcher.BeginInvoke(() =>
                    _mainViewModel?.ReportExitSaveProgress(disk.Options.MountPoint, overallFraction, diskFraction, totalBytes))));
        }
        catch (Exception ex)
        {
            // MountManager.Dispose has already tried every disk; whatever failed is logged there.
            // The window is locked in the exiting state, so the process must still terminate.
            _logger.LogError(ex, "Disposing the mounted disks failed during shutdown.");
        }

        if (!saveFailures.IsEmpty)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, saveFailures),
                Loc.Get("Tray.SaveFailedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        // The environment changes made on the way out are announced on a background thread; give the
        // announcement a moment so programs started afterwards (e.g. from Explorer) see them.
        await Task.Run(() => TempDirResetService.WaitForPendingBroadcasts(TimeSpan.FromSeconds(3)));

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
        // Each step is isolated: a failure here (e.g. SaveSettings hitting a locked settings file)
        // must not skip the remaining steps, and above all must not prevent the caller from
        // reaching MountManager.Dispose, which performs the final save of every disk.
        RunTeardownStep(() =>
        {
            if (_sessionEndingSaveHandler != null)
            {
                SystemEvents.SessionEnding -= _sessionEndingSaveHandler.OnSessionEnding;
            }
        });
        RunTeardownStep(() =>
        {
            if (_mountManager != null && _trayIconController != null)
            {
                _mountManager.ActivityDetected -= _trayIconController.OnActivityDetected;
            }
        });
        // Safety net for exits that bypass ShutdownAsync; a no-op once it has restored everything.
        RunTeardownStep(() => _mainViewModel?.RestoreAllEnvRedirects());
        RunTeardownStep(() => _cliPipeServer?.Dispose());
        RunTeardownStep(() => _mainViewModel?.SaveSettings());
        RunTeardownStep(() => _trayTooltipController?.Dispose());
        RunTeardownStep(() => _trayIconController?.Dispose());
        RunTeardownStep(() => _mainViewModel?.Dispose());
    }

    /// <summary>
    /// Runs one shutdown teardown step, logging instead of propagating any exception so the
    /// remaining steps and the final disk save still run.
    /// </summary>
    /// <param name="step">The teardown step to run.</param>
    private void RunTeardownStep(Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A shutdown teardown step failed; continuing.");
        }
    }
}
