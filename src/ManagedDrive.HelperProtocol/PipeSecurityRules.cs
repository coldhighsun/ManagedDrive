using System.IO.Pipes;
using System.Security.Principal;

namespace ManagedDrive.HelperProtocol;

/// <summary>
/// Security rules shared by the solution's named pipes. Pipe names are machine-wide and
/// well-known, so while the real server isn't listening any local user can create a pipe under
/// the same name; a client must therefore check who created the server end before trusting it
/// with a request.
/// </summary>
public static class PipeSecurityRules
{
    /// <summary>
    /// The <c>BUILTIN\Administrators</c> group, the default owner of objects an elevated process
    /// creates.
    /// </summary>
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// The <c>LocalSystem</c> account the helper service runs as.
    /// </summary>
    private static readonly SecurityIdentifier LocalSystemSid = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>
    /// Whether a pipe owned by <paramref name="owner"/> may be trusted with a request. A standard
    /// user can only make themselves the owner of an object they create, so an owner of
    /// <c>BUILTIN\Administrators</c> or <c>LocalSystem</c> proves a privileged creator — who could
    /// read any request anyway — while any other owner must be <paramref name="trustedUser"/>.
    /// </summary>
    /// <param name="owner">The pipe's owner, or <c>null</c> if it couldn't be read.</param>
    /// <param name="trustedUser">
    /// The one unprivileged owner to trust as well (typically the calling user), or <c>null</c>
    /// to trust only privileged owners.
    /// </param>
    /// <returns><c>true</c> if a request may be sent to the pipe.</returns>
    public static bool IsTrustedOwner(SecurityIdentifier? owner, SecurityIdentifier? trustedUser) =>
        owner is not null && (owner == AdministratorsSid || owner == LocalSystemSid || owner == trustedUser);

    /// <summary>
    /// Reads the owner of the server end of the connected <paramref name="pipe"/>.
    /// </summary>
    /// <param name="pipe">A connected pipe.</param>
    /// <returns>The owner, or <c>null</c> if the pipe's security descriptor couldn't be read.</returns>
    public static SecurityIdentifier? TryGetOwner(PipeStream pipe)
    {
        try
        {
            return pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
