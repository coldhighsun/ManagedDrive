namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// The folders and redirections a disk ends up with after the user ticks presets, and which
/// presets an existing disk already corresponds to. Pure, so the edit dialog's behaviour can be
/// unit tested.
/// </summary>
public static class PresetSelection
{
    /// <summary>
    /// Finds the presets whose folders and redirections a disk already has, so the edit dialog can
    /// tick them. A preset with neither folders nor redirections is never detected.
    /// </summary>
    /// <param name="presets">The presets to look for.</param>
    /// <param name="folders">The disk's folders.</param>
    /// <param name="redirects">The disk's redirections.</param>
    /// <returns>The ids of the presets that are fully present.</returns>
    public static IReadOnlyList<string> Detect(
        IReadOnlyList<DiskPreset> presets, IReadOnlyList<string> folders, IReadOnlyList<EnvRedirect> redirects)
    {
        var detected = new List<string>();
        foreach (var preset in presets)
        {
            if (preset.Folders.Count + preset.EnvRedirects.Count == 0)
            {
                continue;
            }

            if (preset.Folders.All(folder => ContainsFolder(folders, folder)) &&
                preset.EnvRedirects.All(redirect => ContainsRedirect(redirects, redirect)))
            {
                detected.Add(preset.Id);
            }
        }

        return detected;
    }

    /// <summary>
    /// Finds what the user's environment currently redirects into a disk: the variable-redirecting
    /// presets that are in effect (see <see cref="Reconcile"/>), and the remaining redirections of the disk that
    /// belong to no preset and do point into it. Folder-only presets never show up, since the
    /// environment cannot tell whether they are in use.
    /// </summary>
    /// <param name="all">Every preset the app offers.</param>
    /// <param name="redirects">The disk's own redirections; the ones no preset owns are reported as custom.</param>
    /// <param name="pointsIntoDisk">Whether the user's environment currently points a redirection into this disk.</param>
    /// <returns>The active presets and custom variables.</returns>
    public static ActiveRedirects DetectActive(
        IReadOnlyList<DiskPreset> all, IReadOnlyList<EnvRedirect> redirects, Func<EnvRedirect, bool> pointsIntoDisk)
    {
        var exclusive = all.Where(IsExclusive).ToList();
        var presetIds = ActiveExclusive(exclusive, pointsIntoDisk).Select(preset => preset.Id).ToList();
        var custom = redirects
            .Where(own => !RedirectsVariable(exclusive, own.Variable))
            .Where(pointsIntoDisk)
            .Select(own => own.Variable)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new() { PresetIds = presetIds, CustomVariables = custom };
    }

    /// <summary>
    /// Whether the user's TEMP points into a disk, whatever TMP says. This is what makes a disk "the
    /// temp disk" for the card badge and for resetting TEMP when the disk goes away.
    /// </summary>
    /// <param name="pointsIntoDisk">Whether the user's environment currently points a redirection into the disk.</param>
    /// <returns><c>true</c> when TEMP points into the disk.</returns>
    public static bool TempPointsIntoDisk(Func<EnvRedirect, bool> pointsIntoDisk) =>
        BuiltInPresets.Temp.EnvRedirects.Any(redirect =>
            string.Equals(redirect.Variable, "TEMP", StringComparison.OrdinalIgnoreCase) && pointsIntoDisk(redirect));

    /// <summary>
    /// Whether a preset can be on only one disk at a time: one that points environment variables
    /// into the disk, since a variable can point at one place only. A preset that only creates
    /// folders (the browser cache) can be on any number of disks.
    /// </summary>
    /// <param name="preset">The preset.</param>
    /// <returns><c>true</c> when the preset redirects variables.</returns>
    public static bool IsExclusive(DiskPreset preset) => preset.EnvRedirects.Count > 0;

    /// <summary>
    /// Splits a disk's folders and redirections for saving: the presets that redirect variables are
    /// kept as ids only, since the environment tells whether they are in effect, and everything
    /// else (folder-only presets, things no preset owns, presets only partly present) stays as is.
    /// </summary>
    /// <param name="all">Every preset the app offers.</param>
    /// <param name="folders">The disk's folders.</param>
    /// <param name="redirects">The disk's redirections.</param>
    /// <returns>The ids to save, and the folders and redirections left over.</returns>
    public static PresetStorage Split(
        IReadOnlyList<DiskPreset> all, IReadOnlyList<string> folders, IReadOnlyList<EnvRedirect> redirects)
    {
        var stored = Detect(all, folders, redirects)
            .Select(id => all.First(preset => preset.Id == id))
            .Where(IsExclusive)
            .ToList();
        if (stored.Count == 0)
        {
            return new() { Folders = folders, EnvRedirects = redirects };
        }

        return new()
        {
            PresetIds = stored.Select(preset => preset.Id).ToList(),
            Folders = folders.Where(folder => !stored.Any(preset => ContainsFolder(preset.Folders, folder))).ToList(),
            EnvRedirects = redirects.Where(redirect => !stored.Any(preset => ContainsRedirect(preset.EnvRedirects, redirect))).ToList(),
        };
    }

    /// <summary>
    /// Inverse of <see cref="Split"/>: adds the folders and redirections of the saved preset ids
    /// to what was saved besides them. Ids that are not known are ignored.
    /// </summary>
    /// <param name="all">Every preset the app offers.</param>
    /// <param name="presetIds">The saved preset ids.</param>
    /// <param name="folders">The saved folders.</param>
    /// <param name="redirects">The saved redirections.</param>
    /// <returns>The disk's full folders and redirections.</returns>
    public static PresetStorage Expand(
        IReadOnlyList<DiskPreset> all,
        IReadOnlyList<string> presetIds,
        IReadOnlyList<string> folders,
        IReadOnlyList<EnvRedirect> redirects)
    {
        var stored = presetIds
            .Select(id => all.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase)))
            .OfType<DiskPreset>()
            .ToList();
        if (stored.Count == 0)
        {
            return new() { Folders = folders, EnvRedirects = redirects };
        }

        var allFolders = folders.ToList();
        var allRedirects = redirects.ToList();
        foreach (var preset in stored)
        {
            allFolders.AddRange(preset.Folders.Where(folder => !ContainsFolder(allFolders, folder)));
            allRedirects.AddRange(preset.EnvRedirects.Where(redirect => !ContainsRedirect(allRedirects, redirect)));
        }

        return new() { PresetIds = stored.Select(preset => preset.Id).ToList(), Folders = allFolders, EnvRedirects = allRedirects };
    }

    /// <summary>
    /// Makes a disk's folders and redirections agree with the environment for the presets that
    /// redirect variables: such a preset is on the disk exactly when all its variables really point
    /// into it (for Temp, TEMP alone is enough). One the disk's settings list but the environment
    /// does not back is dropped; one the environment backs but the settings lack (set through the
    /// "use as temp" action, say) is added.
    /// Everything else, including folder-only presets and things no preset owns, is left alone.
    /// </summary>
    /// <param name="all">Every preset the app offers.</param>
    /// <param name="folders">The disk's folders.</param>
    /// <param name="redirects">The disk's redirections.</param>
    /// <param name="pointsIntoDisk">Whether the user's environment currently points a redirection into this disk.</param>
    /// <returns>The folders and redirections that match the environment.</returns>
    public static PresetSelectionResult Reconcile(
        IReadOnlyList<DiskPreset> all,
        IReadOnlyList<string> folders,
        IReadOnlyList<EnvRedirect> redirects,
        Func<EnvRedirect, bool> pointsIntoDisk)
    {
        var exclusive = all.Where(IsExclusive).ToList();
        var shared = all.Where(preset => !IsExclusive(preset)).ToList();
        var active = ActiveExclusive(exclusive, pointsIntoDisk);

        var keptFolders = folders
            .Where(folder => !exclusive.Any(preset => ContainsFolder(preset.Folders, folder)) ||
                shared.Any(preset => ContainsFolder(preset.Folders, folder)))
            .ToList();
        var keptRedirects = redirects
            .Where(own => !RedirectsVariable(exclusive, own.Variable))
            .ToList();

        foreach (var preset in active)
        {
            foreach (var folder in preset.Folders.Where(folder => !ContainsFolder(keptFolders, folder)))
            {
                keptFolders.Add(folder);
            }

            keptRedirects.AddRange(preset.EnvRedirects.Where(redirect => !ContainsRedirect(keptRedirects, redirect)));
        }

        return new() { Folders = keptFolders, EnvRedirects = keptRedirects };
    }

    /// <summary>
    /// Takes presets away from a disk because another disk is claiming them. A preset is on the
    /// disk when the disk redirects any of its variables, even if the rest is missing. Its variables
    /// are removed; its folders are removed too unless a preset that stays on the disk needs them.
    /// </summary>
    /// <param name="all">Every preset the app offers.</param>
    /// <param name="folders">The disk's folders.</param>
    /// <param name="redirects">The disk's redirections.</param>
    /// <param name="claimed">The presets another disk now has; only the exclusive ones are taken.</param>
    /// <returns>What the disk keeps and what was taken away.</returns>
    public static PresetReleaseResult Release(
        IReadOnlyList<DiskPreset> all,
        IReadOnlyList<string> folders,
        IReadOnlyList<EnvRedirect> redirects,
        IReadOnlyList<DiskPreset> claimed)
    {
        var released = claimed
            .Where(preset => IsExclusive(preset) &&
                preset.EnvRedirects.Any(r => redirects.Any(own => string.Equals(own.Variable, r.Variable, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (released.Count == 0)
        {
            return new() { Folders = folders, EnvRedirects = redirects };
        }

        var variables = released
            .SelectMany(preset => preset.EnvRedirects)
            .Select(r => r.Variable)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var keptRedirects = redirects
            .Where(own => !variables.Contains(own.Variable, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var staying = all.Where(preset => Detect([preset], folders, keptRedirects).Count > 0 && !released.Contains(preset)).ToList();
        var keptFolders = folders
            .Where(folder => !released.Any(preset => ContainsFolder(preset.Folders, folder)) ||
                staying.Any(preset => ContainsFolder(preset.Folders, folder)))
            .ToList();

        return new()
        {
            Folders = keptFolders,
            EnvRedirects = keptRedirects,
            ReleasedPresetIds = released.Select(preset => preset.Id).ToList(),
            ReleasedVariables = variables,
        };
    }

    /// <summary>
    /// Computes the folders and redirections for the ticked presets. Whatever the disk has that
    /// belongs to none of <paramref name="all"/> (for instance something set up by hand) is kept;
    /// whatever belongs to a preset that is not ticked is dropped.
    /// </summary>
    /// <param name="all">Every preset the dialog offers.</param>
    /// <param name="selected">The ticked presets.</param>
    /// <param name="baseFolders">The folders the disk has now; empty for a new disk.</param>
    /// <param name="baseRedirects">The redirections the disk has now; empty for a new disk.</param>
    /// <returns>The resulting folders and redirections, and the variables that clash.</returns>
    public static PresetSelectionResult Apply(
        IReadOnlyList<DiskPreset> all,
        IReadOnlyList<DiskPreset> selected,
        IReadOnlyList<string> baseFolders,
        IReadOnlyList<EnvRedirect> baseRedirects)
    {
        var merged = PresetComposer.Merge(selected);
        var conflicts = merged.Conflicts.ToList();

        var folders = baseFolders
            .Where(folder => !all.Any(preset => ContainsFolder(preset.Folders, folder)))
            .ToList();
        foreach (var folder in merged.Folders)
        {
            if (!ContainsFolder(folders, folder))
            {
                folders.Add(folder);
            }
        }

        var redirects = baseRedirects
            .Where(redirect => !all.Any(preset => ContainsRedirect(preset.EnvRedirects, redirect)))
            .ToList();
        foreach (var redirect in merged.EnvRedirects)
        {
            var existing = redirects.Find(r => string.Equals(r.Variable, redirect.Variable, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                redirects.Add(redirect);
            }
            else if (!SamePath(existing.SubPath, redirect.SubPath) &&
                !conflicts.Contains(redirect.Variable, StringComparer.OrdinalIgnoreCase))
            {
                conflicts.Add(redirect.Variable);
            }
        }

        return new() { Folders = folders, EnvRedirects = redirects, Conflicts = conflicts };
    }

    /// <summary>
    /// Picks the presets, among those that redirect variables, that are in effect: all their
    /// variables point into the disk, or, for the Temp preset, TEMP does (see <see cref="TempPointsIntoDisk"/>).
    /// </summary>
    /// <param name="exclusive">The variable-redirecting presets.</param>
    /// <param name="pointsIntoDisk">Whether the user's environment currently points a redirection into the disk.</param>
    /// <returns>The presets that are in effect.</returns>
    private static List<DiskPreset> ActiveExclusive(IEnumerable<DiskPreset> exclusive, Func<EnvRedirect, bool> pointsIntoDisk) =>
        exclusive
            .Where(preset => preset.EnvRedirects.All(pointsIntoDisk) ||
                (preset.Id == BuiltInPresets.Temp.Id && TempPointsIntoDisk(pointsIntoDisk)))
            .ToList();

    /// <summary>
    /// Whether one of the presets redirects a variable.
    /// </summary>
    /// <param name="presets">The presets to search.</param>
    /// <param name="variable">The variable name.</param>
    /// <returns><c>true</c> when a preset redirects it, ignoring case.</returns>
    private static bool RedirectsVariable(IEnumerable<DiskPreset> presets, string variable) =>
        presets.Any(preset => preset.EnvRedirects.Any(r => string.Equals(r.Variable, variable, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Whether <paramref name="folders"/> contains <paramref name="folder"/>.
    /// </summary>
    /// <param name="folders">The folders to search.</param>
    /// <param name="folder">The folder to look for.</param>
    /// <returns><c>true</c> when present, ignoring case and surrounding slashes.</returns>
    private static bool ContainsFolder(IReadOnlyList<string> folders, string folder) =>
        folders.Any(candidate => SamePath(candidate, folder));

    /// <summary>
    /// Whether <paramref name="redirects"/> contains the same variable pointing at the same folder.
    /// </summary>
    /// <param name="redirects">The redirections to search.</param>
    /// <param name="redirect">The redirection to look for.</param>
    /// <returns><c>true</c> when present, ignoring case and surrounding slashes.</returns>
    private static bool ContainsRedirect(IReadOnlyList<EnvRedirect> redirects, EnvRedirect redirect) =>
        redirects.Any(candidate =>
            string.Equals(candidate.Variable, redirect.Variable, StringComparison.OrdinalIgnoreCase) &&
            SamePath(candidate.SubPath, redirect.SubPath));

    /// <summary>
    /// Compares two relative paths the way Windows does, ignoring case and surrounding slashes.
    /// </summary>
    /// <param name="a">The first path.</param>
    /// <param name="b">The second path.</param>
    /// <returns><c>true</c> when they are the same folder.</returns>
    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim('\\', '/'), b.Trim('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What the environment currently redirects into a disk: see <see cref="PresetSelection.DetectActive"/>.
/// </summary>
public sealed record ActiveRedirects
{
    /// <summary>
    /// Gets the ids of the variable-redirecting presets whose variables all point into the disk.
    /// </summary>
    public IReadOnlyList<string> PresetIds { get; init; } = [];

    /// <summary>
    /// Gets the variables, owned by no preset, that point into the disk.
    /// </summary>
    public IReadOnlyList<string> CustomVariables { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether nothing is redirected into the disk.
    /// </summary>
    public bool IsEmpty => PresetIds.Count == 0 && CustomVariables.Count == 0;
}

/// <summary>
/// A disk's preset ids, folders and redirections as saved: see <see cref="PresetSelection.Split"/>.
/// </summary>
public sealed record PresetStorage
{
    /// <summary>
    /// Gets the ids of the variable-redirecting presets.
    /// </summary>
    public IReadOnlyList<string> PresetIds { get; init; } = [];

    /// <summary>
    /// Gets the folders not covered by <see cref="PresetIds"/>.
    /// </summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>
    /// Gets the redirections not covered by <see cref="PresetIds"/>.
    /// </summary>
    public IReadOnlyList<EnvRedirect> EnvRedirects { get; init; } = [];
}

/// <summary>
/// The outcome of <see cref="PresetSelection.Release"/>.
/// </summary>
public sealed record PresetReleaseResult
{
    /// <summary>
    /// Gets the folders the disk keeps.
    /// </summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>
    /// Gets the redirections the disk keeps.
    /// </summary>
    public IReadOnlyList<EnvRedirect> EnvRedirects { get; init; } = [];

    /// <summary>
    /// Gets the ids of the presets taken away.
    /// </summary>
    public IReadOnlyList<string> ReleasedPresetIds { get; init; } = [];

    /// <summary>
    /// Gets the variables that stop pointing into the disk and must be put back.
    /// </summary>
    public IReadOnlyList<string> ReleasedVariables { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether nothing was taken away.
    /// </summary>
    public bool IsEmpty => ReleasedPresetIds.Count == 0;
}

/// <summary>
/// The outcome of <see cref="PresetSelection.Apply"/>.
/// </summary>
public sealed record PresetSelectionResult
{
    /// <summary>
    /// Gets the folders the disk should have.
    /// </summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>
    /// Gets the redirections the disk should have.
    /// </summary>
    public IReadOnlyList<EnvRedirect> EnvRedirects { get; init; } = [];

    /// <summary>
    /// Gets the variables that are pointed at two different folders.
    /// </summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];
}
