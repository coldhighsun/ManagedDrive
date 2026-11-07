namespace ManagedDrive.App.Services;

/// <summary>
/// What a row of the "restore environment variables" menu is.
/// </summary>
public enum EnvRestoreMenuEntryKind
{
    /// <summary>Restores one group; <see cref="EnvRestoreMenuEntry.Group"/> is set.</summary>
    Group,

    /// <summary>A disabled note shown when there is nothing to restore.</summary>
    Nothing,

    /// <summary>A disabled note shown when the redirected variables could not be read.</summary>
    Unreadable,

    /// <summary>A separator line before <see cref="All"/>.</summary>
    Separator,

    /// <summary>Restores every group.</summary>
    All,
}

/// <summary>
/// One row of the "restore environment variables" menu, described independently of the UI toolkit
/// so the main window and the tray menu list the same rows.
/// </summary>
/// <param name="Kind">What the row is.</param>
/// <param name="Group">The group a <see cref="EnvRestoreMenuEntryKind.Group"/> row restores; otherwise <c>null</c>.</param>
public sealed record EnvRestoreMenuEntry(EnvRestoreMenuEntryKind Kind, EnvRestoreGroup? Group = null)
{
    /// <summary>
    /// Lays out the menu for the groups that can be restored.
    /// </summary>
    /// <param name="groups">The restorable groups.</param>
    /// <param name="readFailed">Whether the groups could not be read, which is not the same as having none.</param>
    /// <returns>
    /// One row per group; a disabled note when there are none or they could not be read; and a
    /// separator plus a "restore all" row when there are several.
    /// </returns>
    public static IReadOnlyList<EnvRestoreMenuEntry> Build(IReadOnlyList<EnvRestoreGroup> groups, bool readFailed = false)
    {
        if (readFailed)
        {
            return [new(EnvRestoreMenuEntryKind.Unreadable)];
        }

        var entries = new List<EnvRestoreMenuEntry>();
        entries.AddRange(groups.Select(group => new EnvRestoreMenuEntry(EnvRestoreMenuEntryKind.Group, group)));

        if (groups.Count == 0)
        {
            entries.Add(new(EnvRestoreMenuEntryKind.Nothing));
        }

        if (groups.Count > 1)
        {
            entries.Add(new(EnvRestoreMenuEntryKind.Separator));
            entries.Add(new(EnvRestoreMenuEntryKind.All));
        }

        return entries;
    }

    /// <summary>
    /// Gets the localized text of the row.
    /// </summary>
    /// <returns>The text; empty for a separator.</returns>
    public string GetText() => Kind switch
    {
        EnvRestoreMenuEntryKind.Group => EnvRestoreGroupText.GetMenuText(Group!),
        EnvRestoreMenuEntryKind.Nothing => Loc.Get("Menu.RestoreEnvNone"),
        EnvRestoreMenuEntryKind.Unreadable => Loc.Get("Menu.RestoreEnvUnreadable"),
        EnvRestoreMenuEntryKind.All => Loc.Get("Menu.RestoreEnvAll"),
        _ => string.Empty,
    };
}
