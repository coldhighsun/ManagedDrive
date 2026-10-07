namespace ManagedDrive.Core.Snapshots;

/// <summary>
/// What <see cref="SnapshotExtractor.Extract"/> wrote to the host.
/// </summary>
/// <param name="Files">Number of files written.</param>
/// <param name="Directories">Number of directories created or already present under the output.</param>
/// <param name="Bytes">Total size of the files written.</param>
/// <param name="SkippedLinks">
/// Number of symbolic links and junctions left out: they point inside the disk, so a copy of one
/// on the host would point at nothing meaningful.
/// </param>
public readonly record struct SnapshotExtractResult(int Files, int Directories, ulong Bytes, int SkippedLinks);

/// <summary>
/// Copies one file, or a whole directory tree, out of a loaded snapshot onto the host file
/// system. Alternate data streams and links are not copied.
/// </summary>
public static class SnapshotExtractor
{
    /// <summary>
    /// Writes <paramref name="sourcePath"/> from <paramref name="map"/> to <paramref name="outputPath"/>.
    /// A file goes to <paramref name="outputPath"/> itself, or into it when that is an existing
    /// directory; a directory's contents go into <paramref name="outputPath"/>, which is created if
    /// missing. Nothing is written if any file would replace an existing one and
    /// <paramref name="overwrite"/> is not set.
    /// </summary>
    /// <param name="map">The snapshot's contents.</param>
    /// <param name="sourcePath">The file or directory to extract, e.g. <c>\Folder\a.txt</c>; <c>\</c> is the whole disk.</param>
    /// <param name="outputPath">Absolute host path to write to.</param>
    /// <param name="overwrite">Whether existing host files may be replaced.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="FileNotFoundException"><paramref name="sourcePath"/> is not in the snapshot, or is a link.</exception>
    /// <exception cref="IOException">
    /// A host path is unusable (a file where a directory is needed or the reverse, an existing file
    /// without <paramref name="overwrite"/>, a name that escapes the output directory) or writing failed.
    /// </exception>
    public static SnapshotExtractResult Extract(FileNodeMap map, string sourcePath, string outputPath, bool overwrite)
    {
        var source = NormalizeSourcePath(sourcePath);
        if (!map.TryGet(source, out var node) || node is null || node.IsStream)
        {
            throw new FileNotFoundException($"'{source}' is not in the snapshot.");
        }

        if (node.ReparseData is not null)
        {
            throw new FileNotFoundException($"'{source}' is a symbolic link or junction, which cannot be extracted.");
        }

        var files = new List<(string HostPath, FileNode Node)>();
        var directories = new List<string>();
        var skippedLinks = 0;

        if (node.IsDirectory)
        {
            var root = FullPath(outputPath);
            directories.Add(root);
            Collect(map, source, root, root, files, directories, ref skippedLinks);
        }
        else
        {
            // A trailing separator names a directory even if it does not exist yet.
            var intoDirectory = Directory.Exists(outputPath) || Path.EndsInDirectorySeparator(outputPath);
            var target = intoDirectory ? Path.Combine(outputPath, node.LeafName) : outputPath;
            var hostPath = FullPath(target);
            if (Path.GetDirectoryName(hostPath) is { } parent)
            {
                directories.Add(parent);
            }

            files.Add((hostPath, node));
        }

        CheckConflicts(files, directories, overwrite);

        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
        }

        var bytes = 0UL;
        foreach (var (hostPath, fileNode) in files)
        {
            bytes += WriteFile(hostPath, fileNode);
        }

        return new(files.Count, node.IsDirectory ? directories.Count : 0, bytes, skippedLinks);
    }

    /// <summary>
    /// Brings a user-typed path into the backslash-rooted, separator-free-at-the-end form used as
    /// <see cref="FileNodeMap"/> keys (<c>Folder/a.txt</c> becomes <c>\Folder\a.txt</c>).
    /// </summary>
    /// <param name="path">The path as typed.</param>
    /// <returns>The map key.</returns>
    internal static string NormalizeSourcePath(string path)
    {
        var normalized = path.Replace('/', '\\').Trim();
        if (!normalized.StartsWith('\\'))
        {
            normalized = "\\" + normalized;
        }

        return normalized.Length > 1 ? normalized.TrimEnd('\\') : normalized;
    }

    /// <summary>
    /// Adds the files and directories below <paramref name="mapDirectory"/> to the plan, mapping
    /// each to its place under <paramref name="hostDirectory"/>.
    /// </summary>
    /// <param name="map">The snapshot's contents.</param>
    /// <param name="mapDirectory">The snapshot directory being walked.</param>
    /// <param name="hostDirectory">Its counterpart on the host.</param>
    /// <param name="root">The output root every host path must stay inside.</param>
    /// <param name="files">Receives each file with the host path it is written to.</param>
    /// <param name="directories">Receives each host directory to create.</param>
    /// <param name="skippedLinks">Incremented for each link left out.</param>
    private static void Collect(
        FileNodeMap map, string mapDirectory, string hostDirectory, string root,
        List<(string HostPath, FileNode Node)> files, List<string> directories, ref int skippedLinks)
    {
        foreach (var (_, child) in map.GetChildren(mapDirectory, null))
        {
            if (child.ReparseData is not null)
            {
                skippedLinks++;
                continue;
            }

            var hostPath = FullPath(Path.Combine(hostDirectory, child.LeafName));
            if (!hostPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"'{child.FilePath}' would be written outside the output directory.");
            }

            if (child.IsDirectory)
            {
                directories.Add(hostPath);
                Collect(map, child.FilePath, hostPath, root, files, directories, ref skippedLinks);
            }
            else
            {
                files.Add((hostPath, child));
            }
        }
    }

    /// <summary>
    /// Fails before anything is written if the host already has something in the way, so a refused
    /// extraction leaves the output untouched.
    /// </summary>
    /// <param name="files">The planned files.</param>
    /// <param name="directories">The planned directories.</param>
    /// <param name="overwrite">Whether existing files may be replaced.</param>
    private static void CheckConflicts(List<(string HostPath, FileNode Node)> files, List<string> directories, bool overwrite)
    {
        foreach (var directory in directories)
        {
            if (File.Exists(directory))
            {
                throw new IOException($"'{directory}' is an existing file, not a directory.");
            }
        }

        var existing = new List<string>();
        foreach (var (hostPath, _) in files)
        {
            if (Directory.Exists(hostPath))
            {
                throw new IOException($"'{hostPath}' is an existing directory, not a file.");
            }

            if (File.Exists(hostPath))
            {
                existing.Add(hostPath);
            }
        }

        if (!overwrite && existing.Count > 0)
        {
            var more = existing.Count > 1 ? $" (and {existing.Count - 1} more)" : string.Empty;
            throw new IOException($"'{existing[0]}' already exists{more}. Re-run with --force to overwrite.");
        }
    }

    /// <summary>
    /// Writes one file's content and last-write time to the host.
    /// </summary>
    /// <param name="hostPath">The host file to create or replace.</param>
    /// <param name="node">The snapshot file to read.</param>
    /// <returns>The number of bytes written.</returns>
    private static ulong WriteFile(string hostPath, FileNode node)
    {
        var size = node.FileInfo.FileSize;
        using (var stream = new FileStream(hostPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (size > 0)
            {
                if (node.FileData is { } content)
                {
                    content.CopyTo(stream, (long)size);
                }
                else
                {
                    stream.SetLength((long)size);
                }
            }
        }

        // FILETIME of 0 means "never set"; anything out of DateTime's range is skipped rather than
        // failing a successful copy over a timestamp.
        var lastWrite = node.FileInfo.LastWriteTime;
        if (lastWrite > 0 && lastWrite <= (ulong)DateTime.MaxValue.ToFileTimeUtc())
        {
            File.SetLastWriteTimeUtc(hostPath, DateTime.FromFileTimeUtc((long)lastWrite));
        }

        return size;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to an absolute path, reporting an unusable one as an
    /// <see cref="IOException"/> like every other host path problem.
    /// </summary>
    /// <param name="path">The path to resolve.</param>
    /// <returns>The absolute path, without a trailing separator.</returns>
    private static string FullPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IOException($"'{path}' is not a usable path: {ex.Message}", ex);
        }
    }
}
