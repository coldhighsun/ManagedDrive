namespace ManagedDrive.Core.FileSystem;

/// <summary>
/// The directories the app is creating itself while a scope is open, so that
/// <see cref="MemoryFileSystem"/> can tell them from the user's own changes.
/// </summary>
internal sealed class AppFolderRegistry
{
    /// <summary>
    /// Any root works: only the part below it is used, and <see cref="Path.GetFullPath(string, string)"/>
    /// does not touch the disk.
    /// </summary>
    private const string FakeRoot = @"X:\";

    /// <summary>
    /// Guards <see cref="_counts"/>.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Keys of the registered directories, with how many open scopes list each. Case-insensitive,
    /// like the file names.
    /// </summary>
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Number of entries in <see cref="_counts"/>, readable without the lock so that checking a
    /// new directory costs one volatile read while no scope is open.
    /// </summary>
    private int _count;

    /// <summary>
    /// Registers the directories at <paramref name="subPaths"/> (and the parents they need) until
    /// the returned scope is disposed.
    /// </summary>
    /// <param name="subPaths">Folders relative to the disk's root, separated by <c>\</c> or <c>/</c>.</param>
    /// <returns>A scope that removes the registration when disposed.</returns>
    internal IDisposable Begin(IEnumerable<string> subPaths)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subPath in subPaths)
        {
            AddKeys(keys, subPath);
        }

        lock (_gate)
        {
            foreach (var key in keys)
            {
                _counts[key] = _counts.GetValueOrDefault(key) + 1;
            }

            Volatile.Write(ref _count, _counts.Count);
        }

        return new Scope(this, keys);
    }

    /// <summary>
    /// Whether a directory just created at <paramref name="key"/> is registered.
    /// </summary>
    /// <param name="key">The normalized path of the new directory.</param>
    /// <returns><c>true</c> if some open scope lists it.</returns>
    internal bool Contains(string key)
    {
        if (Volatile.Read(ref _count) == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _counts.ContainsKey(key);
        }
    }

    /// <summary>
    /// Adds the keys of <paramref name="subPath"/> and of each parent it needs, in the form
    /// <see cref="MemoryFileSystem.Create"/> sees them. The path is normalized the way Windows does
    /// before it reaches the driver (<c>.</c> and <c>..</c> segments folded, trailing dots and
    /// spaces dropped), because the folder is created through the drive and the driver is handed
    /// the result, not the text the options hold. A sub-path Windows rejects adds nothing: its
    /// creation fails anyway.
    /// </summary>
    /// <param name="keys">The set to add to.</param>
    /// <param name="subPath">A folder relative to the disk's root.</param>
    private static void AddKeys(HashSet<string> keys, string subPath)
    {
        string full;
        try
        {
            full = Path.GetFullPath(subPath.Replace('/', '\\'), FakeRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        var key = string.Empty;
        foreach (var segment in full[FakeRoot.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            key = $"{key}\\{segment}";
            keys.Add(key);
        }
    }

    /// <summary>
    /// Removes one scope's registrations.
    /// </summary>
    /// <param name="keys">The keys the scope added.</param>
    private void End(HashSet<string> keys)
    {
        lock (_gate)
        {
            foreach (var key in keys)
            {
                var remaining = _counts.GetValueOrDefault(key) - 1;
                if (remaining > 0)
                {
                    _counts[key] = remaining;
                }
                else
                {
                    _counts.Remove(key);
                }
            }

            Volatile.Write(ref _count, _counts.Count);
        }
    }

    /// <summary>
    /// An open registration; disposing it ends it exactly once.
    /// </summary>
    /// <param name="owner">The registry it belongs to.</param>
    /// <param name="keys">The keys it added.</param>
    private sealed class Scope(AppFolderRegistry owner, HashSet<string> keys) : IDisposable
    {
        /// <summary>
        /// Non-zero once <see cref="Dispose"/> ran, so a second call does nothing.
        /// </summary>
        private int _disposed;

        /// <summary>
        /// Ends the registration.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.End(keys);
            }
        }
    }
}
