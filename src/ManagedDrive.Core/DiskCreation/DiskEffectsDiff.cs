namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// What has to be done to a mounted disk when its folders and redirections change. Folders that
/// are no longer wanted are left on the disk, since they may hold cache data.
/// </summary>
public sealed record DiskEffectsChange
{
    /// <summary>
    /// Gets the folders to create.
    /// </summary>
    public IReadOnlyList<string> AddedFolders { get; init; } = [];

    /// <summary>
    /// Gets the redirections to apply: new variables, and variables that now point at another folder.
    /// </summary>
    public IReadOnlyList<EnvRedirect> AddedRedirects { get; init; } = [];

    /// <summary>
    /// Gets the variables that are no longer redirected and must be put back.
    /// </summary>
    public IReadOnlyList<string> RemovedVariables { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether there is nothing to do.
    /// </summary>
    public bool IsEmpty => AddedFolders.Count == 0 && AddedRedirects.Count == 0 && RemovedVariables.Count == 0;
}

/// <summary>
/// Compares a disk's folders and redirections before and after an edit.
/// </summary>
public static class DiskEffectsDiff
{
    /// <summary>
    /// Works out what changed.
    /// </summary>
    /// <param name="oldFolders">The folders before the edit.</param>
    /// <param name="oldRedirects">The redirections before the edit.</param>
    /// <param name="newFolders">The folders after the edit.</param>
    /// <param name="newRedirects">The redirections after the edit.</param>
    /// <returns>The folders and redirections to add, and the variables to put back.</returns>
    public static DiskEffectsChange Compute(
        IReadOnlyList<string>? oldFolders,
        IReadOnlyList<EnvRedirect>? oldRedirects,
        IReadOnlyList<string>? newFolders,
        IReadOnlyList<EnvRedirect>? newRedirects)
    {
        oldFolders ??= [];
        oldRedirects ??= [];
        newFolders ??= [];
        newRedirects ??= [];

        var addedFolders = newFolders
            .Where(folder => !oldFolders.Any(old => SamePath(old, folder)))
            .ToList();
        var addedRedirects = newRedirects
            .Where(redirect => !oldRedirects.Any(old =>
                string.Equals(old.Variable, redirect.Variable, StringComparison.OrdinalIgnoreCase) &&
                SamePath(old.SubPath, redirect.SubPath)))
            .ToList();
        var removedVariables = oldRedirects
            .Select(old => old.Variable)
            .Where(variable => !newRedirects.Any(redirect => string.Equals(redirect.Variable, variable, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new() { AddedFolders = addedFolders, AddedRedirects = addedRedirects, RemovedVariables = removedVariables };
    }

    /// <summary>
    /// Compares two relative paths the way Windows does, ignoring case and surrounding slashes.
    /// </summary>
    /// <param name="a">The first path.</param>
    /// <param name="b">The second path.</param>
    /// <returns><c>true</c> when they are the same folder.</returns>
    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim('\\', '/'), b.Trim('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
