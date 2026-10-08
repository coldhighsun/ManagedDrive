namespace ManagedDrive.App.Services;

/// <summary>
/// Localized menu text for <see cref="EnvRestoreGroup"/>s.
/// </summary>
public static class EnvRestoreGroupText
{
    /// <summary>
    /// Gets the name of a group: the name of the preset that owns it, or "Other" for the rest.
    /// </summary>
    /// <param name="group">The group to name.</param>
    /// <returns>The localized name.</returns>
    public static string GetName(EnvRestoreGroup group) =>
        group.Id == EnvRestoreGroups.OtherId ? Loc.Get("Menu.RestoreEnvOther") : Loc.Get($"Preset.{group.Id}.Name");

    /// <summary>
    /// Gets the message that tells the user how a restore went, naming the disks that lost presets.
    /// </summary>
    /// <param name="report">What the restore did.</param>
    /// <returns>The localized message; <see cref="EnvRestoreResult.Failed"/> is the only one to show as a warning.</returns>
    public static string GetResultMessage(EnvRestoreReport report)
    {
        var message = report.Result switch
        {
            EnvRestoreResult.Restored => Loc.Get("Msg.RestoreEnvSuccess"),
            EnvRestoreResult.NothingToRestore => Loc.Get("Msg.RestoreEnvNothing"),
            _ => Loc.Get("Msg.RestoreEnvFailed"),
        };

        return report.ReleasedFrom.Count == 0
            ? message
            : $"{message} {Loc.Format("Msg.RestoreEnvDisksChanged", string.Join(", ", report.ReleasedFrom))}";
    }

    /// <summary>
    /// Gets the menu text of a group: its name followed by the variables it restores.
    /// </summary>
    /// <param name="group">The group to describe.</param>
    /// <returns>The localized text, for example <c>Temp directory (TEMP, TMP)</c>.</returns>
    public static string GetMenuText(EnvRestoreGroup group) => $"{GetName(group)} ({string.Join(", ", group.Variables)})";
}
