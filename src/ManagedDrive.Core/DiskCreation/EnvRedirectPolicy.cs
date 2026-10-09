using System.Text.RegularExpressions;

namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// Why an <see cref="EnvRedirect"/> or a preset folder is not acceptable.
/// </summary>
public enum EnvRedirectError
{
    /// <summary>The value is acceptable.</summary>
    None = 0,

    /// <summary>The variable name is empty or contains characters other than letters, digits and underscore.</summary>
    BadVariable,

    /// <summary>The variable is one the system depends on; redirecting it could break the user's session.</summary>
    ReservedVariable,

    /// <summary>The folder is empty, rooted, contains a colon, or climbs out of the disk with <c>..</c>.</summary>
    BadSubPath,
}

/// <summary>
/// Validation and path rules for environment-variable redirections and preset folders. Pure
/// functions so they can be unit tested without a registry or a mounted disk.
/// </summary>
public static partial class EnvRedirectPolicy
{
    /// <summary>
    /// The longest variable name accepted.
    /// </summary>
    private const int MaxVariableLength = 128;

    /// <summary>
    /// Variables that must never be redirected. TEMP and TMP are not among them: the temp preset
    /// redirects them like any other cache variable.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "COMSPEC", "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "USERPROFILE", "APPDATA",
        "LOCALAPPDATA", "PROGRAMDATA", "PROGRAMFILES", "HOMEDRIVE", "HOMEPATH", "USERNAME",
        "COMPUTERNAME", "PSMODULEPATH",
    };

    /// <summary>
    /// Matches a plain variable name.
    /// </summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableNamePattern();

    /// <summary>
    /// Checks one redirection.
    /// </summary>
    /// <param name="redirect">The redirection to check.</param>
    /// <returns><see cref="EnvRedirectError.None"/> when it is acceptable.</returns>
    public static EnvRedirectError Validate(EnvRedirect redirect)
    {
        var name = redirect.Variable;
        if (string.IsNullOrEmpty(name) || name.Length > MaxVariableLength || !VariableNamePattern().IsMatch(name))
        {
            return EnvRedirectError.BadVariable;
        }

        if (Reserved.Contains(name))
        {
            return EnvRedirectError.ReservedVariable;
        }

        return IsValidSubPath(redirect.SubPath) ? EnvRedirectError.None : EnvRedirectError.BadSubPath;
    }

    /// <summary>
    /// Whether <paramref name="subPath"/> is a folder that stays inside a disk.
    /// </summary>
    /// <param name="subPath">A path relative to the disk's root.</param>
    /// <returns><c>true</c> for a non-empty relative path without colons, invalid characters or <c>..</c> segments.</returns>
    public static bool IsValidSubPath(string? subPath)
    {
        if (string.IsNullOrWhiteSpace(subPath) || subPath.Contains(':') || Path.IsPathRooted(subPath) ||
            subPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || subPath.AsSpan().IndexOfAny('*', '?') >= 0)
        {
            return false;
        }

        foreach (var segment in subPath.Split('\\', '/'))
        {
            if (segment == ".." || (segment.Length > 0 && segment.Trim().Length == 0))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the folder a redirection or preset folder resolves to on a given mount point.
    /// </summary>
    /// <param name="mountPoint">The mount point, such as <c>R:</c> or a directory path.</param>
    /// <param name="subPath">A valid relative path.</param>
    /// <returns>The absolute path, for example <c>R:\npm-cache</c>.</returns>
    public static string Resolve(string mountPoint, string subPath) =>
        Path.Combine(mountPoint.TrimEnd('\\', '/') + "\\", subPath.Replace('/', '\\').Trim('\\'));

    /// <summary>
    /// Whether a variable's value is the folder a redirection names on a disk. The one place that
    /// decides this, shared by the preset reconciliation and the disk card badge.
    /// </summary>
    /// <param name="value">The variable's value as stored (environment references are expanded), or <c>null</c> when it is not set.</param>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <param name="redirect">The redirection to look for.</param>
    /// <returns><c>true</c> when the value is that folder, ignoring case and a trailing slash.</returns>
    public static bool IsRedirectedInto(string? value, string mountPoint, EnvRedirect redirect)
    {
        if (value is null || !IsValidSubPath(redirect.SubPath))
        {
            return false;
        }

        return string.Equals(
            Environment.ExpandEnvironmentVariables(value).TrimEnd('\\', '/'),
            Resolve(mountPoint, redirect.SubPath).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }
}
