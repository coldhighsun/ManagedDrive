using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ManagedDrive.Service;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the helper service's pipe security and how it decodes its settings.
/// </summary>
public sealed class HelperPipeServiceTests
{
    /// <summary>
    /// The pipe denies remote clients, still admits local users, and is owned by LocalSystem so
    /// clients can tell it from a pipe another user created under the same name.
    /// </summary>
    [Fact]
    public void CreatePipeSecurity_Always_DeniesRemoteClientsAdmitsLocalUsersAndIsOwnedByLocalSystem()
    {
        var security = HelperPipeService.CreatePipeSecurity();

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Select(r => (Sid: (SecurityIdentifier)r.IdentityReference, r.AccessControlType, r.PipeAccessRights))
            .ToList();
        Assert.Contains(
            (new SecurityIdentifier(WellKnownSidType.NetworkSid, null), AccessControlType.Deny, PipeAccessRights.FullControl),
            rules);
        Assert.Contains(
            (new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), AccessControlType.Allow, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize),
            rules);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), security.GetOwner(typeof(SecurityIdentifier)));
    }

    /// <summary>
    /// A nonzero DWORD turns a setting on.
    /// </summary>
    [Fact]
    public void IsEnabledSetting_NonzeroDword_ReturnsTrue()
    {
        Assert.True(HelperPipeService.IsEnabledSetting(1));
    }

    /// <summary>
    /// A zero DWORD leaves a setting off.
    /// </summary>
    [Fact]
    public void IsEnabledSetting_ZeroDword_ReturnsFalse()
    {
        Assert.False(HelperPipeService.IsEnabledSetting(0));
    }

    /// <summary>
    /// A missing value leaves a setting off.
    /// </summary>
    [Fact]
    public void IsEnabledSetting_Missing_ReturnsFalse()
    {
        Assert.False(HelperPipeService.IsEnabledSetting(null));
    }

    /// <summary>
    /// A value of another type (e.g. a string "1") is not a DWORD and leaves the setting off.
    /// </summary>
    [Fact]
    public void IsEnabledSetting_NotADword_ReturnsFalse()
    {
        Assert.False(HelperPipeService.IsEnabledSetting("1"));
    }
}
