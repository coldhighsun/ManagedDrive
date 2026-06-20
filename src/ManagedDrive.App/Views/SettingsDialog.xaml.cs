using System.Windows;
using ManagedDrive.App.Models;
using ManagedDrive.App.Services;

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

        Result = new AppConfiguration
        {
            RunAtStartup = runAtStartup,
            Disks = _original.Disks,
        };

        DialogResult = true;
    }
}