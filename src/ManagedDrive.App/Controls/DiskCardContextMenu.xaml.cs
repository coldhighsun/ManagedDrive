using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ManagedDrive.App.Controls;

/// <summary>
/// The context menu of a disk card. Its "Presets" submenu is filled with one check item per preset and
/// ticked from what the user's environment points at each time the menu opens.
/// </summary>
public partial class DiskCardContextMenu
{
    /// <summary>
    /// Whether a preset change from a disk card's context menu is being applied.
    /// </summary>
    private static bool _presetChangeRunning;

    /// <summary>
    /// Initializes the menu and creates the preset check items.
    /// </summary>
    public DiskCardContextMenu()
    {
        InitializeComponent();
        CreatePresetItems();
        Opened += OnOpened;
    }

    /// <summary>
    /// Creates the check items of the "Presets" submenu, one per preset. Done as soon as the menu
    /// exists, so the item is a submenu header from the start and opens on hover.
    /// </summary>
    private void CreatePresetItems()
    {
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
            item.SetResourceReference(ToolTipProperty, $"Preset.{preset.Id}.Desc");
            item.Click += PresetItem_Click;
            PresetsItem.Items.Add(item);
        }
    }

    /// <summary>
    /// Ticks the "Presets" submenu's items according to what the user's environment points at, and
    /// disables them on a read-only disk.
    /// </summary>
    private void OnOpened(object sender, RoutedEventArgs e)
    {
        if (PlacementTarget is FrameworkElement { DataContext: DiskViewModel disk, Tag: MainViewModel main })
        {
            RefreshPresetItems(PresetsItem, disk, main);
        }
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
    private static async void PresetItem_Click(object sender, RoutedEventArgs e)
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
}
