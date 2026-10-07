namespace ManagedDrive.Core.FileSystem;

/// <summary>
/// Parsing and normalization of NTFS alternate-data-stream names. Every stream is stored as its
/// own <see cref="FileNode"/> under the key <c>owner:stream</c> (e.g. <c>\a.txt:Zone.Identifier</c>);
/// a colon never occurs in an ordinary Windows file name, so a key containing one is always a
/// stream key.
/// </summary>
internal static class AlternateStreamName
{
    /// <summary>
    /// Separates a file's path from the name of one of its streams.
    /// </summary>
    internal const char Separator = ':';

    /// <summary>
    /// The only stream type this file system supports; the others (<c>$INDEX_ALLOCATION</c>, ...)
    /// have no meaning on a RAM disk.
    /// </summary>
    private const string DataType = "$DATA";

    /// <summary>
    /// The characters that may not appear in a stream name besides the separator itself.
    /// </summary>
    private static readonly char[] InvalidNameChars = ['\\', '/', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Brings a name received from WinFsp into the form used as a <see cref="FileNodeMap"/> key:
    /// <c>file::$DATA</c> becomes <c>file</c>, <c>file:s:$DATA</c> becomes <c>file:s</c>.
    /// </summary>
    /// <param name="fileName">Absolute path as received from WinFsp.</param>
    /// <param name="key">The map key; <paramref name="fileName"/> itself when nothing needed changing.</param>
    /// <returns>
    /// <c>false</c> if the name is not a valid file or stream name (a colon in a directory
    /// component, an empty stream name, or a stream type other than <c>$DATA</c>).
    /// </returns>
    internal static bool TryNormalize(string fileName, out string key)
    {
        key = fileName;
        var firstColon = fileName.IndexOf(Separator);
        if (firstColon < 0)
        {
            return true;
        }

        if (firstColon < fileName.LastIndexOf('\\'))
        {
            return false;
        }

        var owner = fileName[..firstColon];
        var rest = fileName.AsSpan(firstColon + 1);
        var typeSeparator = rest.IndexOf(Separator);
        var name = typeSeparator < 0 ? rest : rest[..typeSeparator];

        if (typeSeparator >= 0)
        {
            var type = rest[(typeSeparator + 1)..];
            if (!type.Equals(DataType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (name.IsEmpty)
            {
                key = owner;
                return owner.Length > 0 && !owner.EndsWith('\\');
            }
        }

        if (!IsValidStreamName(name) || owner.Length == 0 || owner.EndsWith('\\'))
        {
            return false;
        }

        key = string.Concat(owner, ":", name);
        return true;
    }

    /// <summary>
    /// Like <see cref="TryNormalize"/> for callers that already hold an open handle and only need a
    /// key to look the node up by: a name that does not normalize is returned unchanged.
    /// </summary>
    /// <param name="fileName">Absolute path as received from WinFsp.</param>
    /// <returns>The map key, or <paramref name="fileName"/> if it is not a valid name.</returns>
    internal static string KeyOrSelf(string fileName) => TryNormalize(fileName, out var key) ? key : fileName;

    /// <summary>
    /// Gets a value indicating whether <paramref name="key"/> names a stream rather than a file or
    /// directory.
    /// </summary>
    /// <param name="key">A normalized map key.</param>
    internal static bool IsStreamKey(string key) => key.Contains(Separator);

    /// <summary>
    /// Gets a value indicating whether <paramref name="key"/> is exactly the normalized key of a
    /// stream. A key with a colon that is not (an empty stream name, a colon in a directory name,
    /// a stray <c>:$DATA</c> suffix) is not a stream, only a node with an unreachable name.
    /// </summary>
    /// <param name="key">A key of a <see cref="FileNodeMap"/>.</param>
    internal static bool IsWellFormedStreamKey(string key) =>
        IsStreamKey(key) && TryNormalize(key, out var normalized) &&
        string.Equals(normalized, key, StringComparison.Ordinal);

    /// <summary>
    /// Gets the path of the file a stream belongs to.
    /// </summary>
    /// <param name="streamKey">A normalized stream key.</param>
    internal static string OwnerOf(string streamKey) => streamKey[..streamKey.IndexOf(Separator)];

    /// <summary>
    /// Gets the name of a stream, without the owner path or separator.
    /// </summary>
    /// <param name="streamKey">A normalized stream key.</param>
    internal static string NameOf(string streamKey) => streamKey[(streamKey.IndexOf(Separator) + 1)..];

    /// <summary>
    /// Checks a stream name: non-empty, no control characters and none of the characters Windows
    /// forbids in a file name.
    /// </summary>
    /// <param name="name">The part of the stream key after the separator.</param>
    private static bool IsValidStreamName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || name.IndexOfAny(InvalidNameChars) >= 0)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c < ' ')
            {
                return false;
            }
        }

        return true;
    }
}
