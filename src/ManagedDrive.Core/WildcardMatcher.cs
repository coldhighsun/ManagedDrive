namespace ManagedDrive.Core;

/// <summary>
/// Case-insensitive glob matcher supporting <c>*</c> (zero or more characters) and
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
    /// <returns>
    /// <c>true</c> when the whole name matches the whole pattern.
    /// </returns>
    public static bool Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name)
    {
        var p = 0;
        var n = 0;
        var starIdx = -1;
        var matchIdx = 0;

        while (n < name.Length)
        {
            if (p < pattern.Length &&
                (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])))
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
    /// <returns>
    /// <c>true</c> when the name matches (or the pattern matches everything).
    /// </returns>
    public static bool Matches(string? pattern, string name)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
        {
            return true;
        }

        return Match(pattern.AsSpan(), name.AsSpan());
    }
}
