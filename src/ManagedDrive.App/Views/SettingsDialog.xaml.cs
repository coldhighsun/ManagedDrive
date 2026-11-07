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

    /// <summary>
    /// Initializes the dialog with the current application configuration.
    /// </summary>
    /// <param name="config">The current configuration to display.</param>
    public SettingsDialog(AppConfiguration config)
    {
        InitializeComponent();
        RunAtStartupBox.IsChecked = StartupManager.IsEnabled;
        StartMinimizedBox.IsChecked = config.StartMinimized;
        CloseToTrayBox.IsChecked = config.CloseToTray;
        ContextMenuEnabledBox.IsChecked = ShellContextMenuManager.IsRegistered;
        AutoCheckForUpdatesBox.IsChecked = config.AutoCheckForUpdates;
        DefaultImageDirectoryBox.Text = config.DefaultImageDirectory ?? string.Empty;
        HelperServiceStatusText.Text = Loc.Get("Settings.HelperServiceChecking");
        _ = UpdateHelperServiceStatusAsync();

        DefaultCompressionLevelBox.Level = config.DefaultCompressionLevel ?? ZstdLevels.Default;

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

        foreach (var style in StyleManager.SupportedStyles)
        {
            StyleBox.Items.Add(new ComboBoxItem { Content = Loc.Get($"Settings.Style.{style.ToUpperInvariant()}"), Tag = style });
        }

        var savedStyle = StyleManager.Resolve(config.UiStyle);
        foreach (ComboBoxItem item in StyleBox.Items)
        {
            if ((string)item.Tag == savedStyle)
            {
                StyleBox.SelectedItem = item;
                break;
            }
        }

        FontBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Settings.Font.Default"), Tag = "" });
        FontBox.SelectedIndex = 0;
        var savedFont = FontManager.Normalize(config.UiFontFamily);
        if (savedFont != null)
        {
            FontBox.SelectedIndex = FontBox.Items.Add(new ComboBoxItem { Content = savedFont, Tag = savedFont });
        }

        _ = LoadInstalledFontsAsync(savedFont);
    }

    /// <summary>
    /// Fills <see cref="FontBox"/> with the installed fonts once they are read, so opening the dialog
    /// doesn't wait for every font file. Until then it holds the default and the saved font, and it keeps
    /// them if the fonts can't be listed. An open dropdown is left alone until it closes, so the rows
    /// don't change under the pointer.
    /// </summary>
    /// <param name="savedFont">The saved font, listed even when it is not installed.</param>
    private async Task LoadInstalledFontsAsync(string? savedFont)
    {
        IReadOnlyList<FontChoice> installed;
        try
        {
            installed = await FontManager.Instance.GetInstalledFontsAsync(LanguageManager.Instance.CurrentLanguage);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not list the installed fonts.");
            return;
        }

        if (!FontBox.IsDropDownOpen)
        {
            FillFontBox(installed, savedFont);
            return;
        }

        void OnDropDownClosed(object? sender, EventArgs e)
        {
            FontBox.DropDownClosed -= OnDropDownClosed;
            FillFontBox(installed, savedFont);
        }

        FontBox.DropDownClosed += OnDropDownClosed;
    }

    /// <summary>
    /// Replaces everything in <see cref="FontBox"/> after the default entry, keeping what is selected.
    /// </summary>
    /// <param name="installed">The installed fonts.</param>
    /// <param name="savedFont">The saved font, listed even when it is not installed.</param>
    private void FillFontBox(IReadOnlyList<FontChoice> installed, string? savedFont)
    {
        var selected = FontBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "";
        var fonts = FontManager.GetChoices(installed, savedFont, name => Loc.Format("Settings.Font.Missing", name));

        FontBox.SelectedIndex = 0;
        while (FontBox.Items.Count > 1)
        {
            FontBox.Items.RemoveAt(1);
        }

        foreach (var font in fonts)
        {
            var index = FontBox.Items.Add(new ComboBoxItem { Content = font.DisplayName, Tag = font.Source });
            if (FontManager.IsSameFont(selected, font.Source))
            {
                FontBox.SelectedIndex = index;
            }
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

        var selectedStyle = StyleBox.SelectedItem is ComboBoxItem { Tag: string st } && st != StyleManager.DefaultStyle ? st : null;
        StyleManager.Instance.Apply(selectedStyle);

        var selectedFont = FontManager.Normalize(FontBox.SelectedItem is ComboBoxItem { Tag: string f } ? f : null);
        FontManager.Instance.Apply(selectedFont);

        Result = new()
        {
            RunAtStartup = runAtStartup,
            StartMinimized = StartMinimizedBox.IsChecked == true,
            CloseToTray = CloseToTrayBox.IsChecked == true,
            Language = selectedTag,
            Theme = selectedTheme,
            UiStyle = selectedStyle,
            UiFontFamily = selectedFont,
            ContextMenuEnabled = contextMenuEnabled,
            AutoCheckForUpdates = AutoCheckForUpdatesBox.IsChecked == true,
            DefaultCompressionLevel = DefaultCompressionLevelBox.Level,
            DefaultImageDirectory = string.IsNullOrWhiteSpace(DefaultImageDirectoryBox.Text) ? null : DefaultImageDirectoryBox.Text,
        };

        DialogResult = true;
    }

    /// <summary>
    /// Returns <paramref name="latest"/> with the fields this dialog edits taken from
    /// <paramref name="edits"/>. Everything else — disk profiles, update-check state, the TEMP
    /// warning flag — may have been written while the dialog was open (a CLI command, the tray
    /// menu), so it must come from the configuration read at save time, not from the snapshot the
    /// dialog was opened with.
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
        UiStyle = edits.UiStyle,
        UiFontFamily = edits.UiFontFamily,
        ContextMenuEnabled = edits.ContextMenuEnabled,
        AutoCheckForUpdates = edits.AutoCheckForUpdates,
        DefaultCompressionLevel = edits.DefaultCompressionLevel,
        DefaultImageDirectory = edits.DefaultImageDirectory,
    };

    /// <summary>
    /// Opens the folder picker when the box is clicked, starting in the folder it holds; the text itself is read-only,
    /// so the setting is always a folder that was picked.
    /// </summary>
    private void DefaultImageDirectoryBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        BrowseDefaultImageDirectory();
    }

    /// <summary>
    /// Opens the folder picker on Enter or Space, so the read-only box can be used without a mouse.
    /// </summary>
    private void DefaultImageDirectoryBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
        {
            e.Handled = true;
            BrowseDefaultImageDirectory();
        }
    }

    /// <summary>
    /// Keeps the clear button live only while the box holds a folder.
    /// </summary>
    private void DefaultImageDirectoryBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ClearDefaultImageDirectoryButton.IsEnabled = DefaultImageDirectoryBox.Text.Length > 0;

    /// <summary>
    /// Empties <see cref="AppConfiguration.DefaultImageDirectory"/>.
    /// </summary>
    private void ClearDefaultImageDirectory_Click(object sender, RoutedEventArgs e) =>
        DefaultImageDirectoryBox.Text = string.Empty;

    /// <summary>
    /// Opens a folder picker to choose <see cref="AppConfiguration.DefaultImageDirectory"/>.
    /// </summary>
    private void BrowseDefaultImageDirectory()
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

}
