namespace ManagedDrive.App.ViewModels;

/// <summary>
/// Stateless helper methods extracted from <see cref="MainViewModel"/>: pure functions with no
/// dependency on instance state (mount manager, disk collection, settings store), pulled out to
/// keep the view model focused on stateful command handling. Consumed via
/// <c>using static ManagedDrive.App.ViewModels.MainViewModelHelpers;</c> in <c>MainViewModel.cs</c>
/// so call sites stay unqualified.
/// </summary>
internal static class MainViewModelHelpers
{
    /// <summary>
    /// Returns whether any disk in <paramref name="otherDisks"/> already has <paramref name="path"/>
    /// set on the field selected by <paramref name="selector"/> (e.g. <see cref="DiskOptions.PersistImagePath"/>
    /// or <see cref="DiskOptions.SourceArchivePath"/>). Shared by every image/archive path
    /// collision check (create, edit, import, CLI mount).
    /// </summary>
    public static bool IsPathInUse(IReadOnlyList<DiskOptions> otherDisks, string path, Func<DiskOptions, string?> selector) =>
        otherDisks.Any(d => selector(d) is { } p && string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds the first free drive letter searching from <c>Z:</c> down to <c>D:</c>, skipping
    /// letters already in use by any Windows drive (mounted RAM disks included, since they show
    /// up in <see cref="DriveInfo.GetDrives"/> like any other volume).
    /// </summary>
    /// <returns>A free mount point (e.g. <c>"Z:"</c>), or <c>null</c> if none is free.</returns>
    public static string? FindFreeDriveLetter()
    {
        var usedLetters = new HashSet<char>(
            DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));

        for (var c = 'Z'; c >= 'D'; c--)
        {
            if (!usedLetters.Contains(c))
            {
                return $"{c}:";
            }
        }

        return null;
    }

    /// <summary>
    /// Shows a modal <see cref="MessageBox"/> with the "ManagedDrive" title and an error icon.
    /// Shared boilerplate for the many failure paths across <see cref="MainViewModel"/> that don't
    /// need a custom title (contrast <c>ExecuteEditDisk</c>'s inline apply-failure message, which
    /// reuses <c>Msg.EditDiskConfirmTitle</c> as its title instead).
    /// </summary>
    public static void ShowError(string message) =>
        MessageBox.Show(message, "ManagedDrive", MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>
    /// Shows a modal <see cref="MessageBox"/> with the "ManagedDrive" title and an info icon.
    /// </summary>
    public static void ShowInfo(string message) =>
        MessageBox.Show(message, "ManagedDrive", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>
    /// Shows a modal <see cref="MessageBox"/> with the "ManagedDrive" title and a warning icon.
    /// </summary>
    public static void ShowWarning(string? message) =>
        MessageBox.Show(message, "ManagedDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
}
