using System.Windows.Controls;
using System.Windows.Input;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="SettingsDialog"/>.
/// </summary>
public partial class SettingsDialog
{
    /// <summary>
    /// Gap between the high-usage warning threshold and the (hidden) reset threshold that
    /// re-arms the warning, providing hysteresis without exposing a second setting to the user.
    /// </summary>
    private const int HighUsageResetGap = 5;

    /// <summary>
    /// Minimum allowed value for the high-usage warning threshold.
    /// </summary>
    private const int HighUsageMinPercent = 50;

    private readonly AppConfiguration _original;
    private int _highUsageWarnPercent;

    /// <summary>
    /// Initializes the dialog with the current application configuration.
    /// </summary>
    /// <param name="config">The current configuration to display.</param>
    public SettingsDialog(AppConfiguration config)
    {
        InitializeComponent();
        _original = config;
        RunAtStartupBox.IsChecked = config.RunAtStartup;
        StartMinimizedBox.IsChecked = config.StartMinimized;

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

        _highUsageWarnPercent = (int)Math.Clamp(config.HighUsageWarnPercent, HighUsageMinPercent, 99);
        HighUsageWarnPercentBox.Text = _highUsageWarnPercent.ToString();
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
        ParseHighUsagePercentBox();

        var runAtStartup = RunAtStartupBox.IsChecked == true;
        StartupManager.SetEnabled(runAtStartup);

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
            HighUsageWarnPercent = _highUsageWarnPercent,
            HighUsageResetPercent = Math.Max(1, _highUsageWarnPercent - HighUsageResetGap),
        };

        DialogResult = true;
    }

    private void PercentBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }

    private void PercentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ParseHighUsagePercentBox();
    }

    private void HighUsageWarnPercentUp_Click(object sender, RoutedEventArgs e)
    {
        ParseHighUsagePercentBox();
        SetHighUsageWarnPercent(_highUsageWarnPercent + 1);
    }

    private void HighUsageWarnPercentDown_Click(object sender, RoutedEventArgs e)
    {
        ParseHighUsagePercentBox();
        SetHighUsageWarnPercent(_highUsageWarnPercent - 1);
    }

    private void SetHighUsageWarnPercent(int value)
    {
        _highUsageWarnPercent = Math.Clamp(value, HighUsageMinPercent, 99);
        HighUsageWarnPercentBox.Text = _highUsageWarnPercent.ToString();
    }

    private void ParseHighUsagePercentBox()
    {
        if (HighUsageWarnPercentBox == null)
        {
            return;
        }

        if (int.TryParse(HighUsageWarnPercentBox.Text, out var warn))
        {
            _highUsageWarnPercent = Math.Clamp(warn, HighUsageMinPercent, 99);
        }
    }
}