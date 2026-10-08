namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// A set of redirected environment variables that are restored together, such as everything one
/// preset redirects.
/// </summary>
/// <param name="Id">
/// The id of the built-in preset that owns the variables, or <see cref="EnvRestoreGroups.OtherId"/>
/// for variables no built-in preset owns.
/// </param>
/// <param name="Variables">The variable names in the group.</param>
public sealed record EnvRestoreGroup(string Id, IReadOnlyList<string> Variables);

/// <summary>
/// Groups redirected environment variables by the built-in preset that redirects them, so the user
/// can restore one preset's variables without touching the others. Pure, so it needs no registry.
/// </summary>
public static class EnvRestoreGroups
{
    /// <summary>
    /// The id of the group that holds redirected variables no built-in preset owns, such as the
    /// custom redirections of a disk.
    /// </summary>
    public const string OtherId = "other";

    /// <summary>
    /// Groups the variables that are redirected right now.
    /// </summary>
    /// <param name="redirected">The names of the redirected variables; compared ignoring case.</param>
    /// <returns>
    /// The non-empty groups in the order of <see cref="BuiltInPresets.All"/>, followed by the group
    /// of variables no preset owns. Each group lists only the variables that are redirected.
    /// </returns>
    public static IReadOnlyList<EnvRestoreGroup> Build(IEnumerable<string> redirected)
    {
        var remaining = new HashSet<string>(redirected, StringComparer.OrdinalIgnoreCase);
        var groups = new List<EnvRestoreGroup>();

        foreach (var preset in BuiltInPresets.All)
        {
            var variables = new List<string>();
            foreach (var redirect in preset.EnvRedirects)
            {
                // Removing as it goes keeps a variable in the first group that names it.
                if (remaining.Remove(redirect.Variable))
                {
                    variables.Add(redirect.Variable);
                }
            }

            if (variables.Count > 0)
            {
                groups.Add(new(preset.Id, variables));
            }
        }

        if (remaining.Count > 0)
        {
            groups.Add(new(OtherId, [.. remaining.Order(StringComparer.OrdinalIgnoreCase)]));
        }

        return groups;
    }
}
