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
    /// Gets whether restoring a group (or all of them) may touch TEMP and TMP, which then need a
    /// note about the Windows defaults.
    /// </summary>
    /// <param name="group">The group to restore; <c>null</c> for every group.</param>
    /// <returns><c>true</c> for all groups and for the temp group, which is where the grouping puts TEMP and TMP.</returns>
    public static bool AffectsTemp(EnvRestoreGroup? group) =>
        group is null || group.Id == BuiltInPresets.Temp.Id;

    /// <summary>
    /// Gets the text of the confirmation dialog, naming what is about to be restored.
    /// </summary>
    /// <param name="group">The group to restore; <c>null</c> for every group.</param>
    /// <returns>The localized text, ending with the question to confirm.</returns>
    public static string GetConfirmBody(EnvRestoreGroup? group)
    {
        var scope = group is null ? Loc.Get("Msg.RestoreEnvScopeAll") : GetMenuText(group);
        return ComposeConfirmBody(
            Loc.Format("Msg.RestoreEnvConfirmBody", scope),
            AffectsTemp(group) ? Loc.Get("Msg.RestoreEnvConfirmTempNote") : null,
            Loc.Get("Msg.RestoreEnvConfirmQuestion"));
    }

    /// <summary>
    /// Puts the parts of the confirmation text together: the body, the optional TEMP note on the
    /// same paragraph, then the question after a blank line.
    /// </summary>
    /// <param name="body">What will be restored and what follows from it.</param>
    /// <param name="tempNote">The note about TEMP and TMP, or <c>null</c> when the restore does not touch them.</param>
    /// <param name="question">The question to confirm.</param>
    /// <returns>The text to show.</returns>
    internal static string ComposeConfirmBody(string body, string? tempNote, string question) =>
        $"{(tempNote is null ? body : $"{body} {tempNote}")}\n\n{question}";

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
