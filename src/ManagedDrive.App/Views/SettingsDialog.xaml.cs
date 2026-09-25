using ManagedDrive.HelperProtocol;
using System.Windows.Controls;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="SettingsDialog"/>.
/// </summary>
public partial class SettingsDialog
{
    /// <summary>
    /// Logger used to record why <see cref="HelperPipeClient.IsServiceAvailable(out string?)"/>
    /// reported the helper service unavailable, since that check otherwise gives no diagnostic
    /// trail when a user reports the service as running.
    /// </summary>
    private static readonly ILogger Logger = AppLog.CreateLogger<SettingsDialog>();

    private static readonly List<ImageCompressionLevel> CompressionLevels = [
        ImageCompressionLevel.None,
        ImageCompressionLevel.Fastest,
        ImageCompressionLevel.Optimal,
        ImageCompressionLevel.SmallestSize,
    ];

    private readonly UpdateCheckService? _updateCheckService;

    /// <summary>
    /// Initializes the dialog with the current application configuration.
    /// </summary>
    /// <param name="config">The current configuration to display.</param>
    /// <param name="updateCheckService">
    /// The update checker backing the "Check for Updates Now" button, or <see langword="null"/>
    /// to disable that button (mirrors <see cref="AboutDialog"/>'s handling of a missing service).
    /// </param>
    public SettingsDialog(AppConfiguration config, UpdateCheckService? updateCheckService = null)
    {
        InitializeComponent();
        _updateCheckService = updateCheckService;
        RunAtStartupBox.IsChecked = StartupManager.IsEnabled;
        StartMinimizedBox.IsChecked = config.StartMinimized;
        CloseToTrayBox.IsChecked = config.CloseToTray;
        ContextMenuEnabledBox.IsChecked = ShellContextMenuManager.IsRegistered;
        AutoCheckForUpdatesBox.IsChecked = config.AutoCheckForUpdates;
        DefaultImageDirectoryBox.Text = config.DefaultImageDirectory ?? string.Empty;
        HelperServiceStatusText.Text = Loc.Get("Settings.HelperServiceChecking");
        _ = UpdateHelperServiceStatusAsync();

        foreach (var level in CompressionLevels)
        {
            DefaultCompressionLevelBox.Items.Add(new CompressionLevelItem(level, Loc.Get(CompressionLevelKey(level))));
        }

        DefaultCompressionLevelBox.SelectedIndex =
            CompressionLevels.IndexOf(config.DefaultCompressionLevel ?? ImageCompressionLevel.Fastest);

        LanguageBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Lang.System"), Tag = "" });
        foreach (var (tag, displayName) in LanguageManager.SupportedLanguages)
        {
            LanguageBox.Items.Add(new ComboBoxItem { Content = displayName, Tag = tag });
        }

        var savedLang = config.Language ?? "";
        foreach (ComboBoxItem item in LanguageBox.Items)
        {
            if ((string)item.Tag == savedLang)
            {
                LanguageBox.SelectedItem = item;
                break;
            }
        }

        if (LanguageBox.SelectedItem == null)
        {
            LanguageBox.SelectedIndex = 0;
        }

        ThemeBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Settings.Theme.System"), Tag = "" });
        ThemeBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Settings.Theme.Light"), Tag = "light" });
        ThemeBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Settings.Theme.Dark"), Tag = "dark" });

        var savedTheme = config.Theme ?? "";
        foreach (ComboBoxItem item in ThemeBox.Items)
        {
            if ((string)item.Tag == savedTheme)
            {
                ThemeBox.SelectedItem = item;
                break;
            }
        }

        if (ThemeBox.SelectedItem == null)
        {
            ThemeBox.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// Pings the helper service off the UI thread and fills in <see cref="HelperServiceStatusText"/>
    /// once it answers, so opening the dialog doesn't block on the pipe connect/read timeouts.
    /// </summary>
    private async Task UpdateHelperServiceStatusAsync()
    {
        var available = await Task.Run(() =>
        {
            var ok = HelperPipeClient.IsServiceAvailable(out var failureReason);
            if (!ok && failureReason != null)
            {
                Logger.LogInformation("Helper service unavailable: {Reason}", failureReason);
            }

            return ok;
        });

        HelperServiceStatusText.Text = available
            ? Loc.Get("Settings.HelperServiceAvailable")
            : Loc.Get("Settings.HelperServiceUnavailable");
    }

    /// <summary>
    /// Gets the settings the user confirmed, or <c>null</c> when the dialog was cancelled. Only the
    /// fields this dialog edits are meaningful; apply them to the latest configuration with
    /// <see cref="ApplyEdits"/> rather than saving this instance as-is.
    /// </summary>
    public AppConfiguration? Result
    {
        get; private set;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        var runAtStartup = RunAtStartupBox.IsChecked == true;
        StartupManager.SetEnabled(runAtStartup);

        var contextMenuEnabled = ContextMenuEnabledBox.IsChecked == true;
        ShellContextMenuManager.SetEnabled(contextMenuEnabled);

        var selectedTag = LanguageBox.SelectedItem is ComboBoxItem { Tag: string t } && !string.IsNullOrEmpty(t) ? t : null;
        LanguageManager.Instance.Apply(selectedTag);

        var selectedTheme = ThemeBox.SelectedItem is ComboBoxItem { Tag: string th } && !string.IsNullOrEmpty(th) ? th : null;
        ThemeManager.Instance.Apply(selectedTheme);

        Result = new()
        {
            RunAtStartup = runAtStartup,
            StartMinimized = StartMinimizedBox.IsChecked == true,
            CloseToTray = CloseToTrayBox.IsChecked == true,
            Language = selectedTag,
            Theme = selectedTheme,
            ContextMenuEnabled = contextMenuEnabled,
            AutoCheckForUpdates = AutoCheckForUpdatesBox.IsChecked == true,
            DefaultCompressionLevel = (DefaultCompressionLevelBox.SelectedItem as CompressionLevelItem)?.Level
                ?? ImageCompressionLevel.Fastest,
            DefaultImageDirectory = string.IsNullOrWhiteSpace(DefaultImageDirectoryBox.Text) ? null : DefaultImageDirectoryBox.Text,
        };

        DialogResult = true;
    }

    /// <summary>
    /// Returns <paramref name="latest"/> with the fields this dialog edits taken from
    /// <paramref name="edits"/>. Everything else — disk profiles, update-check state, the TEMP
    /// warning flag — may have been written while the dialog was open (a CLI command, the tray
    /// menu, the dialog's own "Check for Updates Now"), so it must come from the configuration
    /// read at save time, not from the snapshot the dialog was opened with.
    /// </summary>
    /// <param name="latest">The configuration currently on disk.</param>
    /// <param name="edits">The dialog's <see cref="Result"/>.</param>
    internal static AppConfiguration ApplyEdits(AppConfiguration latest, AppConfiguration edits) => latest with
    {
        RunAtStartup = edits.RunAtStartup,
        StartMinimized = edits.StartMinimized,
        CloseToTray = edits.CloseToTray,
        Language = edits.Language,
        Theme = edits.Theme,
        ContextMenuEnabled = edits.ContextMenuEnabled,
        AutoCheckForUpdates = edits.AutoCheckForUpdates,
        DefaultCompressionLevel = edits.DefaultCompressionLevel,
        DefaultImageDirectory = edits.DefaultImageDirectory,
    };

    /// <summary>
    /// Opens a folder picker to choose <see cref="AppConfiguration.DefaultImageDirectory"/>.
    /// </summary>
    private void BrowseDefaultImageDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = Loc.Get("Settings.DefaultImageDirectory"),
            InitialDirectory = DefaultImageDirectoryBox.Text,
        };

        if (dlg.ShowDialog() == true)
        {
            DefaultImageDirectoryBox.Text = dlg.FolderName;
        }
    }

    private static string CompressionLevelKey(ImageCompressionLevel level) => level switch
    {
        ImageCompressionLevel.None => "CompressionLevel.None",
        ImageCompressionLevel.Fastest => "CompressionLevel.Fastest",
        ImageCompressionLevel.SmallestSize => "CompressionLevel.SmallestSize",
        _ => "CompressionLevel.Optimal",
    };

    private sealed record CompressionLevelItem(ImageCompressionLevel Level, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>
    /// Opens the app's log directory (<c>%APPDATA%\ManagedDrive\logs</c>) in Windows Explorer,
    /// creating it first if logging hasn't written anything yet.
    /// </summary>
    private void OpenLogDirectory_Click(object sender, RoutedEventArgs e)
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ManagedDrive", "logs");
        Directory.CreateDirectory(logDirectory);
        Process.Start("explorer.exe", logDirectory);
    }

    /// <summary>
    /// Runs an immediate, user-initiated update check (bypassing the daily throttle, like
    /// <see cref="AboutDialog"/>'s check) and reports the result via a message box.
    /// </summary>
    private async void CheckForUpdatesNow_Click(object sender, RoutedEventArgs e)
    {
        if (_updateCheckService == null)
        {
            return;
        }

        CheckForUpdatesNowButton.IsEnabled = false;
        try
        {
            var (success, info) = await _updateCheckService.CheckSilentlyAsync();
            var message = (success, info) switch
            {
                (true, not null) => Loc.Format("About.UpdateAvailable", info.Version),
                (true, null) => Loc.Get("Settings.UpToDate"),
                _ => Loc.Get("Settings.UpdateCheckFailed"),
            };
            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            CheckForUpdatesNowButton.IsEnabled = true;
        }
    }
}
