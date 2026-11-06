using System.Windows.Controls;

namespace ManagedDrive.App;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Initializes the main window and binds the supplied view model.
    /// </summary>
    /// <param name="viewModel">The view model to bind as <c>DataContext</c>.</param>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        WindowMaximizeHelper.HookMaximizeBehavior(this);
        StateChanged += (_, _) => UpdateMaximizeIcon();
        UpdateMaximizeIcon();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Swaps the caption button's glyph between "maximize" and "restore" to match the current
    /// <see cref="Window.WindowState"/>, since it toggles between the two rather than having
    /// separate buttons for each.
    /// </summary>
    private void UpdateMaximizeIcon() =>
        MaximizeIcon.Text = WindowState == WindowState.Maximized ? "" : "";

    /// <summary>
    /// Keeps the selected disk in view: a clicked notification can select one that is scrolled away.
    /// </summary>
    /// <param name="sender">The disk list.</param>
    /// <param name="e">Unused.</param>
    private void DiskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiskList.SelectedItem is { } selected)
        {
            DiskList.ScrollIntoView(selected);
        }
    }

    private void ImportBtn_Click(object sender, RoutedEventArgs e) => OpenAttachedContextMenu(sender);

    /// <summary>
    /// Opens the "restore environment variables" menu, with one item per group that can be restored
    /// and, when there are several, an item that restores all of them. Rebuilt on every click so it
    /// lists what is redirected right now.
    /// </summary>
    /// <param name="sender">The restore button.</param>
    /// <param name="e">Unused.</param>
    private void RestoreEnvBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } || DataContext is not MainViewModel main)
        {
            return;
        }

        menu.Items.Clear();
        var readable = main.TryGetEnvRestoreGroups(out var groups);
        foreach (var entry in EnvRestoreMenuEntry.Build(groups, readFailed: !readable))
        {
            switch (entry.Kind)
            {
                case EnvRestoreMenuEntryKind.Separator:
                    menu.Items.Add(new Separator());
                    break;
                case EnvRestoreMenuEntryKind.Nothing:
                case EnvRestoreMenuEntryKind.Unreadable:
                    menu.Items.Add(new MenuItem { Header = entry.GetText(), IsEnabled = false });
                    break;
                default:
                    menu.Items.Add(new MenuItem
                    {
                        Header = entry.GetText(),
                        Command = main.RestoreEnvCommand,
                        CommandParameter = entry.Group,
                    });
                    break;
            }
        }

        OpenAttachedContextMenu(sender);
    }

    /// <summary>
    /// Shows the "copy" drop cursor only for a single existing file, and only when the view model
    /// isn't already busy with another operation or exiting; anything else (multiple files,
    /// non-file data, a directory, a since-deleted path) is rejected so the drop target doesn't
    /// imply support it doesn't have.
    /// </summary>
    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = CanAcceptDrop() && TryGetSingleDroppedFilePath(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Imports a single file dropped onto the window via <see cref="MainViewModel.ImportDroppedFileAsync"/>,
    /// which dispatches to the disk-image or archive import flow based on extension. Ignored while
    /// the view model is already busy with another operation or exiting, so a drop can't start a
    /// second operation concurrent with one already in flight.
    /// </summary>
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (TryGetSingleDroppedFilePath(e) is not { } path || DataContext is not MainViewModel viewModel || !CanAcceptDrop())
        {
            return;
        }

        e.Handled = true;
        await viewModel.ImportDroppedFileAsync(path);
    }

    /// <summary>
    /// Whether the view model can currently accept a dropped file: not already running another
    /// busy-overlay operation, and not in the middle of the exit save.
    /// </summary>
    private bool CanAcceptDrop() => DataContext is MainViewModel { BusyOverlay.IsBusy: false, IsExiting: false };

    private static string? TryGetSingleDroppedFilePath(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
        {
            return null;
        }

        return File.Exists(files[0]) ? files[0] : null;
    }

    private void OpenAttachedContextMenu(object sender)
    {
        if (sender is FrameworkElement { ContextMenu: not null } btn)
        {
            btn.ContextMenu.DataContext = DataContext;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }
}
