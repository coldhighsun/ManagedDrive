namespace ManagedDrive.App.Services;

/// <summary>
/// Tracks what the user's environment redirects into one disk for the card badge, recomputing only
/// when the answer can have changed.
/// </summary>
/// <param name="cache">The environment variable cache to read.</param>
internal sealed class RedirectBadgeTracker(UserEnvVarCache cache)
{
    /// <summary>
    /// The cache generation the current <see cref="Active"/> was computed from.
    /// </summary>
    private long _generation = -1;

    /// <summary>
    /// The mount point <see cref="Active"/> was computed for.
    /// </summary>
    private string? _mountPoint;

    /// <summary>
    /// The disk redirections <see cref="Active"/> was computed from.
    /// </summary>
    private IReadOnlyList<EnvRedirect>? _redirects;

    /// <summary>
    /// Gets what is redirected into the disk as of the last <see cref="Update"/>.
    /// </summary>
    public ActiveRedirects Active { get; private set; } = new();

    /// <summary>
    /// Brings <see cref="Active"/> up to date.
    /// </summary>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <param name="redirects">The redirections the disk's options list.</param>
    /// <returns><c>true</c> when <see cref="Active"/> changed.</returns>
    public bool Update(string mountPoint, IReadOnlyList<EnvRedirect>? redirects)
    {
        var generation = cache.Generation;
        if (generation == _generation && mountPoint == _mountPoint && ReferenceEquals(redirects, _redirects))
        {
            return false;
        }

        var active = PresetSelection.DetectActive(
            BuiltInPresets.All, redirects ?? [], redirect => cache.PointsInto(mountPoint, redirect));

        _generation = generation;
        _mountPoint = mountPoint;
        _redirects = redirects;
        if (active.PresetIds.SequenceEqual(Active.PresetIds) &&
            active.CustomVariables.SequenceEqual(Active.CustomVariables))
        {
            return false;
        }

        Active = active;
        return true;
    }
}
