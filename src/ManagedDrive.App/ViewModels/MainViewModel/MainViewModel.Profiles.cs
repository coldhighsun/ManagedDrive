namespace ManagedDrive.App.ViewModels;

/// <summary>
/// Saved-profile bookkeeping: merging, conversion, retention and persistence.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Returns a <see cref="DiskProfile"/> snapshot for every currently active disk.
    /// </summary>
    /// <returns>
    /// A sequence of <see cref="DiskProfile"/> representing all mounted disks.
    /// </returns>
    public IEnumerable<DiskProfile> GetProfiles() => Disks.Select(vm => ToProfile(vm.Disk.Options));

    /// <summary>
    /// Combines the profiles of the mounted disks with saved profiles that aren't mounted, so the
    /// latter survive a settings save. A not-mounted profile is dropped when a mounted disk
    /// supersedes it: same backing image or archive (the same disk was mounted again, possibly at
    /// another letter), or same mount point when the profile is identified by its letter — it
    /// auto-mounts (keeping both would make them race for the letter on the next startup), or has
    /// no backing file (a non-persistent disk has nothing but its letter to tell it apart, so
    /// keeping it would pile up a copy per session). A backed profile with auto-mount off is kept
    /// even when another disk now uses its letter: it never claims the letter by itself, and
    /// dropping it would silently lose the options remembered for its image.
    /// </summary>
    /// <param name="mounted">Profiles of the currently mounted disks; always kept, in order.</param>
    /// <param name="unmounted">Saved profiles that aren't mounted.</param>
    /// <returns>
    /// <paramref name="mounted"/> followed by every profile in <paramref name="unmounted"/> that
    /// isn't superseded by one of them.
    /// </returns>
    internal static List<DiskProfile> MergeProfiles(IReadOnlyList<DiskProfile> mounted, IEnumerable<DiskProfile> unmounted)
    {
        var merged = mounted.ToList();
        foreach (var profile in unmounted)
        {
            if (!merged.Any(p => Supersedes(p, profile)))
            {
                merged.Add(profile);
            }
        }
        return merged;
    }

    /// <summary>
    /// Drops from <paramref name="unmounted"/> every profile that <paramref name="mounted"/>
    /// supersedes (by the rules of <see cref="MergeProfiles"/>). Called whenever a disk is added,
    /// however it was mounted, so a superseded profile doesn't come back once that disk is
    /// unmounted again (e.g. with its image deleted).
    /// </summary>
    /// <param name="unmounted">Saved profiles that aren't mounted; modified in place.</param>
    /// <param name="mounted">Profile of the disk that was just mounted.</param>
    internal static void RemoveSupersededProfiles(List<DiskProfile> unmounted, DiskProfile mounted) =>
        unmounted.RemoveAll(profile => Supersedes(mounted, profile));

    /// <summary>
    /// Whether the mounted disk's profile <paramref name="kept"/> makes the not-mounted
    /// <paramref name="candidate"/> obsolete; see <see cref="MergeProfiles"/>.
    /// </summary>
    /// <param name="kept">Profile of a mounted disk.</param>
    /// <param name="candidate">A saved profile that isn't mounted.</param>
    /// <returns><c>true</c> if <paramref name="candidate"/> should no longer be saved.</returns>
    private static bool Supersedes(DiskProfile kept, DiskProfile candidate)
    {
        return SamePath(kept.PersistImagePath, candidate.PersistImagePath) ||
            SamePath(kept.SourceArchivePath, candidate.SourceArchivePath) ||
            (IsIdentifiedByLetter(candidate) &&
                string.Equals(kept.MountPoint, candidate.MountPoint, StringComparison.OrdinalIgnoreCase));

        static bool IsIdentifiedByLetter(DiskProfile profile) =>
            profile.AutoMount || !HasBackingFile(profile);

        static bool SamePath(string? a, string? b) =>
            a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="profile"/> is backed by an image or a source archive.
    /// </summary>
    /// <param name="profile">The profile to check.</param>
    /// <returns><c>true</c> if the profile has an image or archive path.</returns>
    private static bool HasBackingFile(DiskProfile profile) =>
        profile.PersistImagePath is not null || profile.SourceArchivePath is not null;

    /// <summary>
    /// Maps a live disk's <see cref="DiskOptions"/> to its persistable <see cref="DiskProfile"/>
    /// counterpart. Inverse of <see cref="ProfileToOptions"/>; kept as a standalone pure function
    /// (rather than inlined in <see cref="GetProfiles"/>) so both directions of this hand-written
    /// field mapping can be round-trip tested independently of any live <see cref="MainViewModel"/>
    /// or mounted disk.
    /// </summary>
    internal static DiskProfile ToProfile(DiskOptions options) =>
        ToProfile(options, PresetSelection.Split(BuiltInPresets.All, options.Folders ?? [], options.EnvRedirects ?? []));

    /// <summary>
    /// Builds the profile for <paramref name="options"/>, saving the presets that redirect
    /// variables as ids only (see <see cref="PresetSelection.Split"/>).
    /// </summary>
    /// <param name="options">The live disk's options.</param>
    /// <param name="stored">The disk's folders and redirections split for saving.</param>
    /// <returns>The profile.</returns>
    private static DiskProfile ToProfile(DiskOptions options, PresetStorage stored) => new()
    {
        MountPoint = options.MountPoint,
        VolumeLabel = options.VolumeLabel,
        CapacityBytes = options.CapacityBytes,
        ReadOnly = options.ReadOnly,
        AutoMount = options.AutoMount,
        PersistImagePath = options.PersistImagePath,
        SourceArchivePath = options.SourceArchivePath,
        AutoSaveIntervalMinutes = options.AutoSaveIntervalMinutes,
        CompressionLevel = options.CompressionLevel,
        CustomZstdLevel = options.CustomZstdLevel,
        MaxSnapshotCount = options.MaxSnapshotCount,
        MaxSnapshotSizeBytes = options.MaxSnapshotSizeBytes,
        HighUsageWarnPercent = options.HighUsageWarnPercent,
        SaveImageOnExit = options.SaveImageOnExit,
        PresetIds = stored.PresetIds.Count == 0 ? null : stored.PresetIds,
        Folders = stored.PresetIds.Count == 0 ? options.Folders : (stored.Folders.Count == 0 ? null : stored.Folders),
        EnvRedirects = stored.PresetIds.Count == 0 ? options.EnvRedirects : (stored.EnvRedirects.Count == 0 ? null : stored.EnvRedirects),
    };

    /// <summary>
    /// Mounts a disk from a saved <see cref="DiskProfile"/> and adds it to the list.
    /// Errors are surfaced via <see cref="StatusText"/>.
    /// </summary>
    /// <param name="profile">The profile to mount.</param>
    /// <param name="progress">An optional progress reporter.</param>
    /// <returns>
    /// <c>true</c> if the disk was mounted successfully; <c>false</c> if mounting failed
    /// (the failure reason is surfaced via <see cref="StatusText"/>).
    /// </returns>
    public async Task<bool> MountFromProfileAsync(DiskProfile profile, IProgress<double>? progress = null)
    {
        _logger.LogInformation("Auto-mounting saved profile {MountPoint}.", profile.MountPoint);
        var options = ProfileToOptions(profile);

        try
        {
            var disk = await MountWithPasswordRetryAsync(options, progress: progress);
            if (disk is null)
            {
                _logger.LogWarning("Auto-mount failed for {MountPoint}.", profile.MountPoint);
                ShowStickyStatus(Loc.Format("Status.AutoMountFailed", profile.MountPoint, Loc.Get("Status.MountFailed")));
                ResetTempIfPointingAt(profile.MountPoint);
                RetainSavedProfiles([profile]);
                return false;
            }

            // The mounted disk now carries this profile; keeping it as not-mounted too would let
            // it outlive an unmount that happens before the next save supersedes it.
            _unmountedProfiles.Remove(profile);
            AddDiskSorted(new(disk));
            ShowStatusUnlessSticky(Loc.Format("Status.Mounted", disk.MountPoint, profile.VolumeLabel));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-mount failed for {MountPoint}.", profile.MountPoint);
            ShowStickyStatus(Loc.Format("Status.AutoMountFailed", profile.MountPoint, ex.Message));
            ResetTempIfPointingAt(profile.MountPoint);
            RetainSavedProfiles([profile]);
            return false;
        }
    }

    /// <summary>
    /// Keeps <paramref name="profiles"/> in the saved settings until a mounted disk supersedes
    /// them. Called from the constructor with every saved profile before any of them is mounted, so a
    /// settings save during the sequential auto-mount (e.g. exiting or creating a disk from the
    /// tray while a password prompt is open) doesn't drop the profiles not yet reached, and so
    /// profiles with auto-mount turned off survive the session instead of being deleted by its
    /// first save. Manual profiles without an image or archive are not kept.
    /// </summary>
    /// <param name="profiles">The saved profiles to keep.</param>
    private void RetainSavedProfiles(IEnumerable<DiskProfile> profiles)
    {
        foreach (var profile in profiles)
        {
            // A manual profile without an image or archive is never mounted again and has no
            // image options to remember, so keeping it would only pile up stale letters.
            if (!profile.AutoMount && !HasBackingFile(profile))
            {
                continue;
            }

            if (!_unmountedProfiles.Contains(profile))
            {
                _unmountedProfiles.Add(profile);
            }
        }
    }

    /// <summary>
    /// Persists the application settings, including a profile for every mounted disk plus every
    /// saved profile that isn't mounted (not reached yet, auto-mount off, or failed to mount) and
    /// hasn't been superseded since.
    /// </summary>
    internal void SaveSettings()
    {
        var profiles = MergeProfiles(GetProfiles().ToList(), _unmountedProfiles);
        _unmountedProfiles.RemoveAll(p => !profiles.Contains(p));
        _settingsStore.Update(current => new()
        {
            RunAtStartup = StartupManager.IsEnabled,
            StartMinimized = current.StartMinimized,
            CloseToTray = current.CloseToTray,
            Language = LanguageManager.Instance.SavedLanguage,
            Theme = ThemeManager.Instance.SavedTheme,
            Disks = profiles,
            TempDirCompatWarningShown = current.TempDirCompatWarningShown,
            ContextMenuEnabled = current.ContextMenuEnabled,
            AutoCheckForUpdates = current.AutoCheckForUpdates,
            LastUpdateCheckUtc = current.LastUpdateCheckUtc,
            SkippedVersion = current.SkippedVersion,
            DefaultCompressionLevel = current.DefaultCompressionLevel,
            DefaultImageDirectory = current.DefaultImageDirectory,
        });
    }

    internal static DiskOptions ProfileToOptions(DiskProfile p) =>
        ProfileToOptions(p, PresetSelection.Expand(BuiltInPresets.All, p.PresetIds ?? [], p.Folders ?? [], p.EnvRedirects ?? []));

    /// <summary>
    /// Builds the options for <paramref name="p"/> with the saved preset ids expanded to their
    /// folders and redirections.
    /// </summary>
    /// <param name="p">The saved profile.</param>
    /// <param name="expanded">The profile's folders and redirections with its presets added.</param>
    /// <returns>The options.</returns>
    private static DiskOptions ProfileToOptions(DiskProfile p, PresetStorage expanded) => new()
    {
        MountPoint = p.MountPoint,
        VolumeLabel = p.VolumeLabel,
        CapacityBytes = p.CapacityBytes,
        ReadOnly = p.ReadOnly,
        AutoMount = p.AutoMount,
        // An archive-sourced disk is never backed by an image; profiles saved with both (by an older
        // "Save image" on an archive disk) would otherwise fail to mount.
        PersistImagePath = p.SourceArchivePath is null ? p.PersistImagePath : null,
        SourceArchivePath = p.SourceArchivePath,
        AutoSaveIntervalMinutes = p.AutoSaveIntervalMinutes,
        CompressionLevel = p.CompressionLevel,
        CustomZstdLevel = p.CustomZstdLevel,
        MaxSnapshotCount = p.MaxSnapshotCount,
        MaxSnapshotSizeBytes = p.MaxSnapshotSizeBytes,
        HighUsageWarnPercent = p.HighUsageWarnPercent,
        SaveImageOnExit = p.SaveImageOnExit,
        Folders = expanded.PresetIds.Count == 0 ? p.Folders : expanded.Folders,
        EnvRedirects = expanded.PresetIds.Count == 0 ? p.EnvRedirects : expanded.EnvRedirects,
    };
}
