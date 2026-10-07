using Microsoft.Win32;

namespace ManagedDrive.App.Services;

/// <summary>
/// Reads and writes the current user's persistent environment variables (<c>HKCU\Environment</c>).
/// An interface so the redirection logic can be tested without touching the registry.
/// </summary>
public interface IUserEnvironment
{
    /// <summary>
    /// Reads a variable exactly as stored.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The stored text and kind, or <c>null</c> when the variable is not set.</returns>
    UserTempValue? Read(string name);

    /// <summary>
    /// Writes a variable.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The text to store.</param>
    /// <param name="kind">The registry value kind.</param>
    void Write(string name, string value, RegistryValueKind kind);

    /// <summary>
    /// Deletes a variable if it is set.
    /// </summary>
    /// <param name="name">The variable name.</param>
    void Delete(string name);

    /// <summary>
    /// Tells running programs (Explorer, new shells) that the environment changed.
    /// </summary>
    void Broadcast();
}

/// <summary>
/// <see cref="IUserEnvironment"/> backed by the registry.
/// </summary>
public sealed class RegistryUserEnvironment : IUserEnvironment
{
    /// <summary>
    /// The registry key, below <c>HKEY_CURRENT_USER</c>, that holds the user's variables.
    /// </summary>
    private const string KeyPath = "Environment";

    /// <inheritdoc />
    public UserTempValue? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string text)
        {
            return null;
        }

        return new(text, key.GetValueKind(name));
    }

    /// <inheritdoc />
    public void Write(string name, string value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("The user environment key is not available.");
        key.SetValue(name, value, kind);
        UserTempCache.Shared.Invalidate();
    }

    /// <inheritdoc />
    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
        UserTempCache.Shared.Invalidate();
    }

    /// <inheritdoc />
    public void Broadcast() => TempDirResetService.BroadcastEnvironmentChange();
}

/// <summary>
/// What <see cref="UserEnvironmentRedirector.ApplyDiskEffects"/> did.
/// </summary>
/// <param name="Applied">The variables now pointing into the disk.</param>
/// <param name="Rejected">Variables left alone because another disk already redirects them.</param>
/// <param name="Failed">Folders or variables that could not be created or written.</param>
public sealed record RedirectResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Rejected, IReadOnlyList<string> Failed)
{
    /// <summary>
    /// Gets a value indicating whether everything requested was done.
    /// </summary>
    public bool IsComplete => Rejected.Count == 0 && Failed.Count == 0;
}

/// <summary>
/// Points per-user environment variables (npm, NuGet and pip caches, ...) into a RAM disk while it
/// is mounted and puts them back afterwards. What each variable held before is stored, so a restore
/// brings back the user's own value and not a hard-coded default, and a crash cannot leave a
/// variable pointing at a drive that is gone: the leftovers are restored on the next start.
/// </summary>
/// <param name="environment">The user's environment.</param>
/// <param name="readBackups">Reads the stored backups.</param>
/// <param name="writeBackups">Replaces the stored backups; returns <c>false</c> when they could not be saved.</param>
public sealed class UserEnvironmentRedirector(
    IUserEnvironment environment,
    Func<IReadOnlyList<EnvRedirectBackup>> readBackups,
    Func<List<EnvRedirectBackup>, bool> writeBackups)
{
    /// <summary>
    /// Serializes changes, since mounts and unmounts run on different threads.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Creates the disk's folders and points its environment variables into it.
    /// </summary>
    /// <param name="options">The mounted disk's options.</param>
    /// <param name="isCurrent">
    /// Asked, while changes are serialized with <see cref="Restore"/>, before each variable is
    /// written; returning <c>false</c> (the disk was unmounted meanwhile) stops without writing
    /// anything more, so a restore that already ran is not undone. <c>null</c> always continues.
    /// </param>
    /// <returns>What was done; <see cref="RedirectResult.IsComplete"/> is <c>true</c> when nothing is left over.</returns>
    public RedirectResult ApplyDiskEffects(DiskOptions options, Func<bool>? isCurrent = null)
    {
        var applied = new List<string>();
        var rejected = new List<string>();
        var failed = new List<string>();

        foreach (var folder in options.Folders ?? [])
        {
            if (!TryCreateFolder(options.MountPoint, folder))
            {
                failed.Add(folder);
            }
        }

        lock (_gate)
        {
            var backups = readBackups().ToList();
            var changed = false;
            foreach (var redirect in options.EnvRedirects ?? [])
            {
                if (isCurrent is not null && !isCurrent())
                {
                    break;
                }

                if (EnvRedirectPolicy.Validate(redirect) != EnvRedirectError.None)
                {
                    failed.Add(redirect.Variable);
                    continue;
                }

                var target = EnvRedirectPolicy.Resolve(options.MountPoint, redirect.SubPath);
                if (!TryCreateFolder(options.MountPoint, redirect.SubPath))
                {
                    failed.Add(redirect.Variable);
                    continue;
                }

                var existing = backups.Find(b => SameName(b.Variable, redirect.Variable));
                if (existing is not null && !SameName(existing.MountPoint, options.MountPoint))
                {
                    rejected.Add(redirect.Variable);
                    continue;
                }

                try
                {
                    // Record what to restore before writing, so a crash in between loses nothing.
                    // Changed on a copy: a backup that could not be saved must not be carried into
                    // the next variable's save, where it would claim a redirection never made.
                    var backup = existing is null
                        ? NewBackup(redirect.Variable, options.MountPoint, target)
                        : existing with { AppliedValue = target };
                    var candidate = backups.ToList();
                    if (existing is null)
                    {
                        candidate.Add(backup);
                    }
                    else
                    {
                        candidate[candidate.IndexOf(existing)] = backup;
                    }

                    if (!writeBackups(candidate))
                    {
                        failed.Add(redirect.Variable);
                        continue;
                    }

                    backups = candidate;
                    changed = true;
                    environment.Write(redirect.Variable, target, RegistryValueKind.String);
                    applied.Add(redirect.Variable);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                {
                    failed.Add(redirect.Variable);
                }
            }

            if (changed)
            {
                environment.Broadcast();
            }
        }

        return new(applied, rejected, failed);
    }

    /// <summary>
    /// Whether a variable currently points at the folder a redirection names on a disk, whoever set it.
    /// </summary>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <param name="redirect">The redirection to look for.</param>
    /// <returns><c>true</c> when the variable is set to that folder, ignoring case and a trailing slash.</returns>
    public bool PointsInto(string mountPoint, EnvRedirect redirect)
    {
        try
        {
            if (environment.Read(redirect.Variable) is not { } current ||
                !EnvRedirectPolicy.IsValidSubPath(redirect.SubPath))
            {
                return false;
            }

            var target = EnvRedirectPolicy.Resolve(mountPoint, redirect.SubPath);
            return string.Equals(
                Environment.ExpandEnvironmentVariables(current.Text).TrimEnd('\\', '/'),
                target.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts back every variable that points into the disk at <paramref name="mountPoint"/>.
    /// </summary>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <returns>The number of variables restored.</returns>
    public int Restore(string mountPoint) => RestoreWhere(backup => SameName(backup.MountPoint, mountPoint));

    /// <summary>
    /// Puts back the named variables where they point into a RAM disk.
    /// </summary>
    /// <param name="variables">The variable names; compared ignoring case.</param>
    /// <param name="broadcast">Whether to announce the change to running programs.</param>
    /// <param name="mountPoint">
    /// Only restore variables that point into the disk at this mount point; <c>null</c> restores
    /// them whichever disk they point into.
    /// </param>
    /// <returns>The number of variables restored.</returns>
    public int RestoreVariables(IReadOnlyCollection<string> variables, bool broadcast = true, string? mountPoint = null) =>
        RestoreWhere(
            backup => variables.Any(name => SameName(name, backup.Variable)) &&
                (mountPoint is null || SameName(backup.MountPoint, mountPoint)),
            broadcast);

    /// <summary>
    /// Puts back every variable that points into any RAM disk, whichever disk it is. Used when the
    /// app exits or the session ends, before the disks go away.
    /// </summary>
    /// <param name="broadcast">
    /// Whether to announce the change to running programs; <c>false</c> when the session is ending.
    /// </param>
    /// <returns>The number of variables restored.</returns>
    public int RestoreAll(bool broadcast = true) => RestoreWhere(_ => true, broadcast);

    /// <summary>
    /// Puts back the variables whose disk is no longer mounted, e.g. after a crash.
    /// </summary>
    /// <param name="isMounted">Whether a disk with the given mount point is mounted now.</param>
    /// <returns>The number of variables restored.</returns>
    public int RestoreDangling(Func<string, bool> isMounted) => RestoreWhere(backup => !isMounted(backup.MountPoint));

    /// <summary>
    /// Restores and forgets every backup that matches <paramref name="selector"/>. A variable the
    /// user changed since is left as it is; its backup is still dropped.
    /// </summary>
    /// <param name="selector">Chooses the backups to restore.</param>
    /// <param name="broadcast">Whether to announce the change to running programs.</param>
    /// <returns>The number of variables written back.</returns>
    private int RestoreWhere(Func<EnvRedirectBackup, bool> selector, bool broadcast = true)
    {
        lock (_gate)
        {
            var backups = readBackups().ToList();
            var selected = backups.Where(selector).ToList();
            if (selected.Count == 0)
            {
                return 0;
            }

            var restored = 0;
            var kept = backups.Except(selected).ToList();
            foreach (var backup in selected)
            {
                try
                {
                    if (environment.Read(backup.Variable) is not { } current ||
                        !string.Equals(current.Text, backup.AppliedValue, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (backup.OriginalText is null)
                    {
                        environment.Delete(backup.Variable);
                    }
                    else
                    {
                        environment.Write(backup.Variable, backup.OriginalText, backup.OriginalKind);
                    }

                    restored++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                {
                    // Keep the backup so the next start tries again.
                    kept.Add(backup);
                }
            }

            writeBackups(kept);
            if (restored > 0 && broadcast)
            {
                environment.Broadcast();
            }

            return restored;
        }
    }

    /// <summary>
    /// Builds the backup for a variable about to be redirected, capturing its current value.
    /// </summary>
    /// <param name="variable">The variable name.</param>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <param name="applied">The value that will be written.</param>
    /// <returns>The backup record.</returns>
    private EnvRedirectBackup NewBackup(string variable, string mountPoint, string applied)
    {
        var original = environment.Read(variable);
        return new()
        {
            Variable = variable,
            MountPoint = mountPoint,
            AppliedValue = applied,
            OriginalText = original?.Text,
            OriginalKind = original?.Kind ?? RegistryValueKind.String,
        };
    }

    /// <summary>
    /// Creates a folder on the disk, including missing parents.
    /// </summary>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <param name="subPath">The folder relative to the disk's root.</param>
    /// <returns><c>false</c> when the folder is invalid or could not be created.</returns>
    private static bool TryCreateFolder(string mountPoint, string subPath)
    {
        if (!EnvRedirectPolicy.IsValidSubPath(subPath))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(EnvRedirectPolicy.Resolve(mountPoint, subPath));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Compares names the way Windows does: ignoring case.
    /// </summary>
    /// <param name="a">The first name.</param>
    /// <param name="b">The second name.</param>
    /// <returns><c>true</c> when they are equal ignoring case.</returns>
    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
