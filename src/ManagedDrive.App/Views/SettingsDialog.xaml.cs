using System.Windows.Controls;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="SettingsDialog"/>.
/// </summary>
public partial class SettingsDialog
{
    private readonly AppConfiguration _original;
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
        _original = config;
        _updateCheckService = updateCheckService;
        RunAtStartupBox.IsChecked = StartupManager.IsEnabled;
        StartMinimizedBox.IsChecked = config.StartMinimized;
        ContextMenuEnabledBox.IsChecked = ShellContextMenuManager.IsRegistered;
        AutoCheckForUpdatesBox.IsChecked = config.AutoCheckForUpdates;

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
    /// Gets the updated <see cref="AppConfiguration"/> after the user confirms.
    /// <c>null</c> when the dialog was cancelled.
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
            Language = selectedTag,
            Theme = selectedTheme,
            Disks = _original.Disks,
            TempDirCompatWarningShown = _original.TempDirCompatWarningShown,
            ContextMenuEnabled = contextMenuEnabled,
            AutoCheckForUpdates = AutoCheckForUpdatesBox.IsChecked == true,
            LastUpdateCheckUtc = _original.LastUpdateCheckUtc,
            SkippedVersion = _original.SkippedVersion,
        };

        DialogResult = true;
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
            var (result, info) = await _updateCheckService.CheckSilentlyAsync();
            var message = result switch
            {
                UpdateCheckResult.UpdateAvailable when info != null => Loc.Format("About.UpdateAvailable", info.Version),
                UpdateCheckResult.UpToDate => Loc.Get("Settings.UpToDate"),
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
