using System.IO.Pipes;
using System.Security.AccessControl;
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
    /// The <c>NETWORK</c> group present in every network logon's token, i.e. in every client
    /// connecting to a pipe remotely over SMB.
    /// </summary>
    private static readonly SecurityIdentifier NetworkSid = new(WellKnownSidType.NetworkSid, null);

    /// <summary>
    /// Creates the security descriptor for a pipe only <paramref name="owner"/> may use: full
    /// control for <paramref name="owner"/>, which is also made the pipe's owner, and nothing for
    /// anyone else, remote clients in particular.
    /// </summary>
    /// <param name="owner">The only principal allowed to connect.</param>
    /// <returns>The security descriptor to create the pipe with.</returns>
    public static PipeSecurity CreateOwnerOnlySecurity(SecurityIdentifier owner)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        DenyRemoteClients(security);
        security.SetOwner(owner);
        return security;
    }

    /// <summary>
    /// Adds a rule to <paramref name="security"/> that denies every remote (network logon)
    /// client, which a named pipe otherwise accepts over SMB whenever its other rules allow the
    /// remote user in.
    /// </summary>
    /// <param name="security">The security descriptor to add the rule to.</param>
    public static void DenyRemoteClients(PipeSecurity security) =>
        security.AddAccessRule(new(NetworkSid, PipeAccessRights.FullControl, AccessControlType.Deny));

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
