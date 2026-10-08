namespace ManagedDrive.App.Services;

/// <summary>
/// Restores redirected environment variables on request, one group at a time. TEMP and TMP go
/// through their own delegate, since without a stored backup they may still need to be reset to the
/// Windows defaults; every other group puts back what its backups recorded.
/// </summary>
/// <param name="redirector">The redirector that holds the backups.</param>
/// <param name="restoreTemp">Restores TEMP and TMP.</param>
/// <param name="isTempOnKnownDisk">
/// Whether TEMP points into one of the app's disks. Asked only when no backup is recorded for TEMP,
/// since a TEMP set by an older version has none but still needs offering. Called by <see cref="GetGroups"/>.
/// </param>
public sealed class EnvRestoreService(
    UserEnvironmentRedirector redirector,
    Func<EnvRestoreResult> restoreTemp,
    Func<bool> isTempOnKnownDisk)
{
    /// <summary>
    /// The variables of the temp preset. <see cref="GetGroups"/> adds them only when TEMP has no backup but sits
    /// on one of the app's disks (a TEMP set by an older version); <see cref="Restore"/> adds them for "restore all".
    /// </summary>
    private static readonly string[] TempVariables = [.. BuiltInPresets.Temp.EnvRedirects.Select(redirect => redirect.Variable)];

    /// <summary>
    /// Gets the groups worth offering: every group with a redirected variable, plus the temp group
    /// when TEMP points into one of the app's disks without a recorded backup.
    /// </summary>
    /// <returns>The groups, in menu order; empty when there is nothing to restore.</returns>
    public IReadOnlyList<EnvRestoreGroup> GetGroups()
    {
        // Backups are read once; the TEMP check (registry, settings) runs only when TEMP has none.
        var redirected = redirector.GetRedirectedVariables();
        var tempRecorded = redirected.Contains("TEMP", StringComparer.OrdinalIgnoreCase);
        return EnvRestoreGroups.Build(!tempRecorded && isTempOnKnownDisk() ? redirected.Concat(TempVariables) : redirected);
    }

    /// <summary>
    /// Restores one group, or all of them.
    /// </summary>
    /// <param name="group">The group to restore; <c>null</c> restores every group.</param>
    /// <returns>What was done, combined over the groups.</returns>
    public EnvRestoreResult Restore(EnvRestoreGroup? group)
    {
        // Not GetGroups(): this may run off the UI thread, and restoring TEMP when it needs nothing
        // is harmless (restoreTemp reports "nothing to restore").
        var groups = group is null
            ? EnvRestoreGroups.Build(redirector.GetRedirectedVariables().Concat(TempVariables))
            : [group];

        return Combine(groups.Select(RestoreOne).ToList());
    }

    /// <summary>
    /// Merges the results of several restores into one.
    /// </summary>
    /// <param name="results">The results to merge.</param>
    /// <returns>
    /// <see cref="EnvRestoreResult.Failed"/> if any failed, otherwise <see cref="EnvRestoreResult.Restored"/>
    /// if any restored something, otherwise <see cref="EnvRestoreResult.NothingToRestore"/>.
    /// </returns>
    internal static EnvRestoreResult Combine(IReadOnlyCollection<EnvRestoreResult> results)
    {
        if (results.Contains(EnvRestoreResult.Failed))
        {
            return EnvRestoreResult.Failed;
        }

        return results.Contains(EnvRestoreResult.Restored) ? EnvRestoreResult.Restored : EnvRestoreResult.NothingToRestore;
    }

    /// <summary>
    /// Restores a single group.
    /// </summary>
    /// <param name="group">The group to restore.</param>
    /// <returns>What was done.</returns>
    private EnvRestoreResult RestoreOne(EnvRestoreGroup group)
    {
        if (group.Id == BuiltInPresets.Temp.Id)
        {
            return restoreTemp();
        }

        return redirector.RestoreVariables(group.Variables) > 0 ? EnvRestoreResult.Restored : EnvRestoreResult.NothingToRestore;
    }
}
