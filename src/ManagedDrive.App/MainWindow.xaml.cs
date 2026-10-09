using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ManagedDrive.App;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan SpeedPopupOpenDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SpeedPopupCloseDelay = TimeSpan.FromMilliseconds(150);

    private DispatcherTimer? _speedPopupOpenTimer;
    private DispatcherTimer? _speedPopupCloseTimer;
    private Popup? _pendingCloseSpeedPopup;

    /// <summary>
    /// Whether a preset change from a disk card's context menu is being applied.
    /// </summary>
    private bool _presetChangeRunning;

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

    private void OverflowBtn_Click(object sender, RoutedEventArgs e) => OpenAttachedContextMenu(sender);

    /// <summary>
    /// Creates the check items of a disk card's "Presets" submenu, one per preset. Done as soon as
    /// the item exists, so it is a submenu header from the start and opens on hover.
    /// </summary>
    /// <param name="sender">The "Presets" menu item.</param>
    /// <param name="e">Unused.</param>
    private void PresetsMenuItem_Initialized(object? sender, EventArgs e)
    {
        if (sender is not MenuItem { HasItems: false } presetsItem)
        {
            return;
        }

        foreach (var preset in BuiltInPresets.All)
        {
            var item = new MenuItem
            {
                IsCheckable = true,
                StaysOpenOnClick = true,
                Tag = preset.Id,
            };

            // By resource reference, so the names follow a language switch while the card lives on.
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, $"Preset.{preset.Id}.Name");
            item.SetResourceReference(FrameworkElement.ToolTipProperty, $"Preset.{preset.Id}.Desc");
            item.Click += PresetItem_Click;
            presetsItem.Items.Add(item);
        }
    }

    /// <summary>
    /// Ticks the "Presets" submenu's items according to what the user's environment points at, and
    /// disables them on a read-only disk.
    /// </summary>
    /// <param name="sender">The disk card's context menu.</param>
    /// <param name="e">Unused.</param>
    private void DiskContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: FrameworkElement { DataContext: DiskViewModel disk, Tag: MainViewModel main } } menu ||
            menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag as string == "PresetsMenu") is not { } presetsItem)
        {
            return;
        }

        RefreshPresetItems(presetsItem, disk, main);
    }

    /// <summary>
    /// Sets each check item of the "Presets" submenu from what the user's environment points at,
    /// and enables them unless the disk is read-only.
    /// </summary>
    /// <param name="presetsItem">The "Presets" menu item.</param>
    /// <param name="disk">The disk the menu was opened on.</param>
    /// <param name="main">The main view model.</param>
    private static void RefreshPresetItems(MenuItem presetsItem, DiskViewModel disk, MainViewModel main)
    {
        var active = main.GetActivePresetIds(disk);
        foreach (var item in presetsItem.Items.OfType<MenuItem>())
        {
            item.IsChecked = item.Tag is string id && active.Contains(id);
            item.IsEnabled = disk.Disk.Options.AppliesPresets();
        }
    }

    /// <summary>
    /// Turns the clicked preset on or off for the disk the context menu was opened on. The menu stays
    /// open so more presets can be changed; its items are ticked again from the environment afterwards,
    /// which also undoes a tick that was refused.
    /// </summary>
    /// <param name="sender">The preset's check item, already toggled.</param>
    /// <param name="e">Unused.</param>
    private async void PresetItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem
            {
                Tag: string id,
                Parent: MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: DiskViewModel disk, Tag: MainViewModel main } } } presetsItem,
            } item)
        {
            // A click while the previous change is still being applied is dropped; the refresh below
            // puts its tick back. The items are not greyed meanwhile, which made the menu flash.
            if (_presetChangeRunning)
            {
                return;
            }

            _presetChangeRunning = true;
            try
            {
                await main.SetPresetAsync(disk, id, item.IsChecked);
            }
            finally
            {
                _presetChangeRunning = false;
                RefreshPresetItems(presetsItem, disk, main);
            }
        }
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

    /// <summary>
    /// Schedules the speed-history popup to open after <see cref="SpeedPopupOpenDelay"/> once the
    /// mouse enters the speed row or the popup itself, cancelling any pending close scheduled for
    /// a different popup. Already-open popups stay open immediately, without re-running the delay.
    /// </summary>
    private void SpeedRow_MouseEnter(object sender, MouseEventArgs e)
    {
        var popup = FindSpeedPopup(sender);
        if (popup is null)
        {
            return;
        }

        if (_pendingCloseSpeedPopup is not null && _pendingCloseSpeedPopup != popup)
        {
            CloseSpeedPopup(_pendingCloseSpeedPopup);
        }

        _speedPopupCloseTimer?.Stop();
        _pendingCloseSpeedPopup = null;

        if (popup.IsOpen)
        {
            return;
        }

        _speedPopupOpenTimer?.Stop();
        _speedPopupOpenTimer = new() { Interval = SpeedPopupOpenDelay };
        _speedPopupOpenTimer.Tick += (_, _) =>
        {
            _speedPopupOpenTimer?.Stop();
            popup.IsOpen = true;
        };
        _speedPopupOpenTimer.Start();
    }

    /// <summary>
    /// Cancels a pending open, and schedules the speed-history popup to close shortly after the
    /// mouse leaves the speed row or the popup itself, so briefly crossing the gap between them
    /// doesn't flicker it shut.
    /// </summary>
    private void SpeedRow_MouseLeave(object sender, MouseEventArgs e)
    {
        var popup = FindSpeedPopup(sender);
        if (popup is null)
        {
            return;
        }

        _speedPopupOpenTimer?.Stop();

        if (!popup.IsOpen)
        {
            return;
        }

        _speedPopupCloseTimer?.Stop();
        _pendingCloseSpeedPopup = popup;
        _speedPopupCloseTimer = new() { Interval = SpeedPopupCloseDelay };
        _speedPopupCloseTimer.Tick += (_, _) =>
        {
            _speedPopupCloseTimer?.Stop();
            CloseSpeedPopup(popup);
            _pendingCloseSpeedPopup = null;
        };
        _speedPopupCloseTimer.Start();
    }

    private static void CloseSpeedPopup(Popup popup) => popup.IsOpen = false;

    /// <summary>
    /// Resolves the speed-history <see cref="Popup"/> for a mouse event raised either by the
    /// speed row itself or by the popup's own content border. The border's <c>Tag</c> is bound to
    /// the popup by name in XAML (<c>Tag="{Binding ElementName=SpeedPopup}"</c>) rather than
    /// resolved here via <c>FrameworkElement.Parent</c> — a <see cref="Popup"/>'s child is hosted
    /// in a separate visual root, so relying on logical-tree parentage to hold at arbitrary
    /// MouseEnter/MouseLeave timing is fragile; the explicit binding is resolved once when the
    /// template loads, before any such event can fire.
    /// </summary>
    private static Popup? FindSpeedPopup(object sender) => sender switch
    {
        FrameworkElement { Tag: Popup popup } => popup,
        _ => null,
    };
}
