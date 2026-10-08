namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// What a mounted disk may have to give up when the variables pointing into it are restored.
/// </summary>
/// <param name="Presets">The variable-redirecting presets with a variable redirected into the disk.</param>
/// <param name="CustomVariables">Redirected variables of the disk that none of those presets accounts for.</param>
public sealed record DiskRestoreCandidates(IReadOnlyList<DiskPreset> Presets, IReadOnlyList<string> CustomVariables)
{
    /// <summary>
    /// Gets a value indicating whether there is nothing to take away.
    /// </summary>
    public bool IsEmpty => Presets.Count == 0 && CustomVariables.Count == 0;
}

/// <summary>
/// Decides which presets and redirections a disk loses when the user restores environment
/// variables, so the disk, its saved profile and the environment agree afterwards. Pure: whether a
/// variable points into the disk is asked through a delegate, so no registry is needed.
/// </summary>
public static class DiskRestorePlanner
{
    /// <summary>
    /// Finds what the environment really backs on a disk, before anything is restored.
    /// </summary>
    /// <param name="all">Every preset the app offers.</param>
    /// <param name="redirects">The redirections the disk lists.</param>
    /// <param name="pointsIntoDisk">Whether a redirection's variable points at the disk's folder right now.</param>
    /// <returns>
    /// The variable-redirecting presets with any variable pointing into the disk (a preset the
    /// environment backs only in part is taken whole), and the redirections pointing into the disk
    /// that none of those presets owns.
    /// </returns>
    public static DiskRestoreCandidates Plan(
        IReadOnlyList<DiskPreset> all,
        IReadOnlyList<EnvRedirect> redirects,
        Func<EnvRedirect, bool> pointsIntoDisk)
    {
        var pointing = GetPointingVariables(redirects, pointsIntoDisk);
        var presets = all
            .Where(preset => PresetSelection.IsExclusive(preset) && preset.EnvRedirects.Any(own => pointing.Contains(own.Variable)))
            .ToList();
        var owned = presets
            .SelectMany(preset => preset.EnvRedirects)
            .Select(own => own.Variable)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var custom = redirects
            .Select(redirect => redirect.Variable)
            .Where(variable => pointing.Contains(variable) && !owned.Contains(variable))
            .ToList();
        return new(presets, custom);
    }

    /// <summary>
    /// Keeps only what was really restored: a preset or custom redirection whose variable still
    /// points into the disk after the restore (because that group was not restored, or the restore
    /// failed or skipped it) stays on the disk, so the disk never claims less than the environment backs.
    /// </summary>
    /// <param name="candidates">What <see cref="Plan"/> found before the restore.</param>
    /// <param name="redirects">The redirections the disk lists.</param>
    /// <param name="pointsIntoDisk">Whether a redirection's variable points at the disk's folder now.</param>
    /// <returns>The presets and custom redirections to take away.</returns>
    public static DiskRestoreCandidates Settle(
        DiskRestoreCandidates candidates,
        IReadOnlyList<EnvRedirect> redirects,
        Func<EnvRedirect, bool> pointsIntoDisk)
    {
        var still = GetPointingVariables(redirects, pointsIntoDisk);
        var presets = candidates.Presets
            .Where(preset => !preset.EnvRedirects.Any(own => still.Contains(own.Variable)))
            .ToList();
        var custom = candidates.CustomVariables.Where(variable => !still.Contains(variable)).ToList();
        return new(presets, custom);
    }

    /// <summary>
    /// Takes away from a disk what <see cref="Settle"/> decided it no longer has: the presets that
    /// redirected the restored variables (as <see cref="PresetSelection.Release"/> does, folders
    /// included unless another preset needs them) and the custom redirections.
    /// </summary>
    /// <param name="all">Every preset the app offers.</param>
    /// <param name="folders">The disk's folders.</param>
    /// <param name="redirects">The disk's redirections.</param>
    /// <param name="settled">What <see cref="Settle"/> returned.</param>
    /// <returns>What the disk keeps; <see cref="PresetReleaseResult.ReleasedPresetIds"/> names the presets taken away.</returns>
    public static PresetReleaseResult Apply(
        IReadOnlyList<DiskPreset> all,
        IReadOnlyList<string> folders,
        IReadOnlyList<EnvRedirect> redirects,
        DiskRestoreCandidates settled)
    {
        var release = PresetSelection.Release(all, folders, redirects, settled.Presets);
        var kept = release.EnvRedirects
            .Where(redirect => !settled.CustomVariables.Contains(redirect.Variable, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return release with { EnvRedirects = kept };
    }

    /// <summary>
    /// Collects the variables that point into the disk, asking about each redirection once.
    /// </summary>
    /// <param name="redirects">The redirections the disk lists.</param>
    /// <param name="pointsIntoDisk">Whether a redirection's variable points at the disk's folder.</param>
    /// <returns>The variable names, compared ignoring case as Windows does.</returns>
    private static HashSet<string> GetPointingVariables(IReadOnlyList<EnvRedirect> redirects, Func<EnvRedirect, bool> pointsIntoDisk) =>
        redirects.Where(pointsIntoDisk).Select(redirect => redirect.Variable).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
