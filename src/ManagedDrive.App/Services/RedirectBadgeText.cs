using System.Text;

namespace ManagedDrive.App.Services;

/// <summary>
/// Builds the text of the disk card's redirect badge.
/// </summary>
internal static class RedirectBadgeText
{
    /// <summary>
    /// Lists what is redirected into a disk, one item per line below a header.
    /// </summary>
    /// <param name="active">What the environment redirects into the disk.</param>
    /// <param name="header">The first line.</param>
    /// <param name="presetName">Resolves a preset id to its display name.</param>
    /// <param name="customName">Formats a custom variable name for display.</param>
    /// <returns>The tooltip text, or an empty string when nothing is redirected.</returns>
    public static string Build(
        ActiveRedirects active, string header, Func<string, string> presetName, Func<string, string> customName)
    {
        if (active.IsEmpty)
        {
            return string.Empty;
        }

        var text = new StringBuilder(header);
        foreach (var id in active.PresetIds)
        {
            text.Append('\n').Append(presetName(id));
        }

        foreach (var variable in active.CustomVariables)
        {
            text.Append('\n').Append(customName(variable));
        }

        return text.ToString();
    }
}
