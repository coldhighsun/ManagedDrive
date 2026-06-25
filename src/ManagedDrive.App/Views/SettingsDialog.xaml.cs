using System.Windows;
using System.Windows.Controls;
using ManagedDrive.App.Localization;
using ManagedDrive.App.Models;
using ManagedDrive.App.Services;
using ModernWpf;

namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="SettingsDialog"/>.
/// </summary>
public partial class SettingsDialog
{
    private readonly AppConfiguration _original;

    /// <summary>
    /// Initializes the dialog with the current application configuration.
    /// </summary>
    /// <param name="config">The current configuration to display.</param>
    public SettingsDialog(AppConfiguration config)
    {
        InitializeComponent();
        _original = config;
        RunAtStartupBox.IsChecked = config.RunAtStartup;

        foreach (var (tag, displayName) in LanguageManager.SupportedLanguages)
        {
            LanguageBox.Items.Add(new ComboBoxItem { Content = displayName, Tag = tag });
        }

        var current = LanguageManager.Instance.CurrentLanguage;
        foreach (ComboBoxItem item in LanguageBox.Items)
        {
            if ((string)item.Tag == current)
            {
                LanguageBox.SelectedItem = item;
                break;
            }
        }

        ThemeBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Theme.System"), Tag = "" });
        ThemeBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Theme.Light"), Tag = "Light" });
        ThemeBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Theme.Dark"), Tag = "Dark" });

        var currentTheme = config.Theme ?? "";
        foreach (ComboBoxItem item in ThemeBox.Items)
        {
            if ((string)item.Tag == currentTheme)
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

        var selectedTag = LanguageBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "en-US";
        if (selectedTag != LanguageManager.Instance.CurrentLanguage)
        {
            LanguageManager.Instance.Apply(selectedTag);
        }

        var themeTag = ThemeBox.SelectedItem is ComboBoxItem { Tag: string t } ? t : "";
        var themeValue = string.IsNullOrEmpty(themeTag) ? null : themeTag;
        ThemeManager.Current.ApplicationTheme = themeValue switch
        {
            "Light" => ApplicationTheme.Light,
            "Dark" => ApplicationTheme.Dark,
            _ => null,
        };

        Result = new AppConfiguration
        {
            RunAtStartup = runAtStartup,
            Language = selectedTag,
            Theme = themeValue,
            Disks = _original.Disks,
        };

        DialogResult = true;
    }
}
