namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// Formats what a disk gets from its presets as short lines for the create/edit dialog. Pure, so
/// the wording rules can be unit tested.
/// </summary>
public static class PresetSummary
{
    /// <summary>
    /// The arrow between a variable and the folder it points at.
    /// </summary>
    private const string Arrow = " → ";

    /// <summary>
    /// Lists folders and variables, one line per folder: variables that point at the same folder
    /// are put together (<c>TEMP, TMP → Temp\</c>), and a folder no variable points at is listed
    /// on its own (<c>BrowserCache\</c>).
    /// </summary>
    /// <param name="folders">The folders created on every mount.</param>
    /// <param name="redirects">The variables pointed into the disk.</param>
    /// <returns>The lines, variables first, in the order they are first seen.</returns>
    public static IReadOnlyList<string> Describe(IReadOnlyList<string> folders, IReadOnlyList<EnvRedirect> redirects)
    {
        var lines = redirects
            .GroupBy(redirect => Normalize(redirect.SubPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{string.Join(", ", group.Select(redirect => redirect.Variable))}{Arrow}{group.Key}\\")
            .ToList();
        lines.AddRange(folders
            .Where(folder => !redirects.Any(redirect => string.Equals(Normalize(redirect.SubPath), Normalize(folder), StringComparison.OrdinalIgnoreCase)))
            .Select(folder => Normalize(folder) + "\\"));
        return lines;
    }

    /// <summary>
    /// Trims the slashes around a relative path.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The path without leading or trailing slashes.</returns>
    private static string Normalize(string path) => path.Trim('\\', '/');
}
