namespace ManagedDrive.App.Services;

/// <summary>
/// A mounted disk as the restore of environment variables sees it, so the logic that decides what
/// a disk loses can run without a real RAM disk or view model.
/// </summary>
public interface IMountedDisk
{
    /// <summary>
    /// Gets the disk's mount point, such as <c>R:</c>.
    /// </summary>
    string MountPoint { get; }

    /// <summary>
    /// Gets a value indicating whether the disk is still in the list of mounted disks.
    /// </summary>
    bool IsMounted { get; }

    /// <summary>
    /// Gets a value indicating whether the disk is being remounted by an edit, so it cannot take a change now.
    /// </summary>
    bool IsRemounting { get; }

    /// <summary>
    /// Gets the options the disk runs with now.
    /// </summary>
    DiskOptions Options { get; }

    /// <summary>
    /// Applies new options to the disk without remounting it.
    /// </summary>
    /// <param name="options">The options to apply.</param>
    /// <returns>The reason they were refused, or <c>null</c> when they were applied.</returns>
    Task<string?> ApplyOptionsAsync(DiskOptions options);

    /// <summary>
    /// Updates what the user sees of the disk after its options changed.
    /// </summary>
    void Refresh();
}

/// <summary>
/// A mounted disk and what it may have to give up when the variables pointing into it are restored.
/// </summary>
/// <param name="Disk">The disk.</param>
/// <param name="Candidates">What the environment backed on the disk before the restore.</param>
public sealed record PlannedDisk(IMountedDisk Disk, DiskRestoreCandidates Candidates);

/// <summary>
/// What taking restored presets away from the disks did.
/// </summary>
/// <param name="ReleasedFrom">One entry per disk that changed, such as <c>R: (Node.js caches)</c>.</param>
/// <param name="NotUpdated">The mount points of disks that still list what the environment no longer backs.</param>
public sealed record DiskTrimOutcome(IReadOnlyList<string> ReleasedFrom, IReadOnlyList<string> NotUpdated);

/// <summary>
/// The outcome of <see cref="DiskRestoreCoordinator.RunAsync{TResult}"/>.
/// </summary>
/// <typeparam name="TResult">What the restore returned.</typeparam>
/// <param name="Result">What the restore returned.</param>
/// <param name="Outcome">Which disks changed and which could not be updated.</param>
public sealed record DiskRestoreRun<TResult>(TResult Result, DiskTrimOutcome Outcome);

/// <summary>
/// Makes mounted disks agree with the environment after the user restores redirected variables:
/// before the restore it notes what each disk has (<see cref="Plan"/>), afterwards it takes away
/// exactly what was restored (<see cref="TrimAsync"/>), using <see cref="DiskRestorePlanner"/> for
/// the decisions. Call both on the UI thread: <see cref="IMountedDisk.IsMounted"/> reads the disk
/// list, and <see cref="TrimAsync"/> relies on resuming there after each disk is updated.
/// </summary>
/// <param name="redirector">Tells whether a variable points into a disk.</param>
/// <param name="presetName">Gives the display name of a preset id.</param>
/// <param name="logger">Receives what could not be done.</param>
public sealed class DiskRestoreCoordinator(UserEnvironmentRedirector redirector, Func<string, string> presetName, ILogger logger)
{
    /// <summary>
    /// Runs a restore so the disks always end up in line with what it did: plans before it, trims
    /// after it, even when it throws midway (some variables may already be back), then hands the
    /// outcome to <paramref name="afterTrim"/>. A restore that threw is rethrown last with its own
    /// stack; if <paramref name="afterTrim"/> fails as well, both are thrown together as an
    /// <see cref="AggregateException"/> (the restore failure first), so neither is lost.
    /// </summary>
    /// <typeparam name="TResult">What the restore returns.</typeparam>
    /// <param name="disks">The mounted disks.</param>
    /// <param name="restore">Puts the variables back.</param>
    /// <param name="afterTrim">Called once the disks were updated, for example to save settings and report disks left unchanged.</param>
    /// <returns>What the restore returned and what happened to the disks.</returns>
    public async Task<DiskRestoreRun<TResult>> RunAsync<TResult>(
        IEnumerable<IMountedDisk> disks, Func<Task<TResult>> restore, Action<DiskTrimOutcome> afterTrim)
    {
        var planned = Plan(disks);

        TResult? result = default;
        Exception? failure = null;
        try
        {
            result = await restore();
        }
        catch (Exception ex)
        {
            // Not handled here: kept so the disks can still be updated, then thrown again below.
            failure = ex;
            logger.LogWarning("Restoring environment variables failed ({Reason}); updating the disks for what was restored.", ex.Message);
        }

        var outcome = await TrimAsync(planned);
        if (failure is null)
        {
            afterTrim(outcome);
            return new(result!, outcome);
        }

        Exception? reportFailure = null;
        try
        {
            afterTrim(outcome);
        }
        catch (Exception ex)
        {
            // Not handled here either: it is thrown below together with the restore failure.
            reportFailure = ex;
        }

        if (reportFailure is not null)
        {
            throw new AggregateException(
                "Restoring environment variables failed, and so did reporting the disk update.", failure, reportFailure);
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        throw failure;
    }

    /// <summary>
    /// Finds what the disks have of the variables the user may restore, judged by what the
    /// environment really points into each disk.
    /// </summary>
    /// <param name="disks">The mounted disks.</param>
    /// <returns>One entry per disk that has something to lose.</returns>
    public IReadOnlyList<PlannedDisk> Plan(IEnumerable<IMountedDisk> disks)
    {
        var planned = new List<PlannedDisk>();
        foreach (var disk in disks)
        {
            var options = disk.Options;
            var candidates = DiskRestorePlanner.Plan(
                BuiltInPresets.All, options.EnvRedirects ?? [], redirect => redirector.PointsInto(options.MountPoint, redirect));
            if (!candidates.IsEmpty)
            {
                planned.Add(new(disk, candidates));
            }
        }

        return planned;
    }

    /// <summary>
    /// Removes from each planned disk the presets and custom redirections whose variables were
    /// restored. A disk that was unmounted meanwhile is skipped (its variables are restored on
    /// unmount); one that is remounting, refuses the change or throws is reported as not updated.
    /// </summary>
    /// <param name="planned">What <see cref="Plan"/> found, before the restore.</param>
    /// <returns>Which disks changed and which could not be updated.</returns>
    public async Task<DiskTrimOutcome> TrimAsync(IReadOnlyList<PlannedDisk> planned)
    {
        var releasedFrom = new List<string>();
        var notUpdated = new List<string>();
        foreach (var (disk, candidates) in planned)
        {
            if (!disk.IsMounted)
            {
                continue;
            }

            if (disk.IsRemounting)
            {
                notUpdated.Add(disk.MountPoint);
                continue;
            }

            try
            {
                var options = disk.Options;
                var redirects = options.EnvRedirects ?? [];
                var settled = DiskRestorePlanner.Settle(
                    candidates, redirects, redirect => redirector.PointsInto(options.MountPoint, redirect));
                if (settled.IsEmpty)
                {
                    continue;
                }

                var release = DiskRestorePlanner.Apply(BuiltInPresets.All, options.Folders ?? [], redirects, settled);
                if (release.Folders.SequenceEqual(options.Folders ?? []) && release.EnvRedirects.SequenceEqual(redirects))
                {
                    // The disk's options changed since the plan and no longer list what was planned.
                    continue;
                }

                var error = await disk.ApplyOptionsAsync(options with
                {
                    Folders = release.Folders.Count == 0 ? null : release.Folders,
                    EnvRedirects = release.EnvRedirects.Count == 0 ? null : release.EnvRedirects,
                });
                if (error is not null)
                {
                    logger.LogWarning("Updating {MountPoint} after restoring its variables failed: {Error}", options.MountPoint, error);
                    notUpdated.Add(options.MountPoint);
                    continue;
                }

                disk.Refresh();
                var lost = release.ReleasedPresetIds.Select(presetName).Concat(settled.CustomVariables);
                releasedFrom.Add($"{options.MountPoint} ({string.Join(", ", lost)})");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Updating {MountPoint} after restoring its variables failed.", disk.MountPoint);
                notUpdated.Add(disk.MountPoint);
            }
        }

        return new(releasedFrom, notUpdated);
    }
}
