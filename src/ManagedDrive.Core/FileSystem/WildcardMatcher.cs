namespace ManagedDrive.Core.FileSystem;

/// <summary>
/// Glob matcher (case-insensitive unless told otherwise) supporting <c>*</c> (zero or more characters) and
/// <c>?</c> (exactly one character). Extracted from <see cref="MemoryFileSystem"/> so the
/// pure matching logic can be reused and unit-tested without a WinFsp file system.
/// </summary>
public static class WildcardMatcher
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="name"/> matches the glob
    /// <paramref name="pattern"/> (case-insensitive, supporting <c>*</c> and <c>?</c>).
    /// </summary>
    /// <param name="pattern">The glob pattern.</param>
    /// <param name="name">The candidate name to test.</param>
    /// <param name="ignoreCase">
    /// <c>true</c> (the default) to compare characters ignoring case; <c>false</c> to compare them exactly.
    /// </param>
    /// <returns>
    /// <c>true</c> when the whole name matches the whole pattern.
    /// </returns>
    public static bool Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name, bool ignoreCase = true)
    {
        var p = 0;
        var n = 0;
        var starIdx = -1;
        var matchIdx = 0;

        while (n < name.Length)
        {
            if (p < pattern.Length &&
                (pattern[p] == '?' || CharsEqual(pattern[p], name[n], ignoreCase)))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p;
                matchIdx = n;
                p++;
            }
            else if (starIdx != -1)
            {
                p = starIdx + 1;
                matchIdx++;
                n = matchIdx;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="name"/> matches the glob
    /// <paramref name="pattern"/>. A <c>null</c>, empty, or <c>*</c> pattern matches everything.
    /// </summary>
    /// <param name="pattern">The glob pattern, or <c>null</c>/<c>*</c> to match everything.</param>
    /// <param name="name">The candidate name to test.</param>
    /// <param name="ignoreCase">
    /// <c>true</c> (the default) to compare characters ignoring case; <c>false</c> to compare them exactly.
    /// </param>
    /// <returns>
    /// <c>true</c> when the name matches (or the pattern matches everything).
    /// </returns>
    public static bool Matches(string? pattern, string name, bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
        {
            return true;
        }

        return Match(pattern.AsSpan(), name.AsSpan(), ignoreCase);
    }

    /// <summary>
    /// Compares two characters, optionally ignoring case.
    /// </summary>
    /// <param name="a">The first character.</param>
    /// <param name="b">The second character.</param>
    /// <param name="ignoreCase">Whether case is ignored.</param>
    /// <returns><c>true</c> when the characters are equal under the chosen comparison.</returns>
    private static bool CharsEqual(char a, char b, bool ignoreCase) =>
        ignoreCase ? char.ToUpperInvariant(a) == char.ToUpperInvariant(b) : a == b;
}
