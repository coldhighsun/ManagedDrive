using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ManagedDrive.HelperProtocol;
using ManagedDrive.Service;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>
    /// Ping needs no caller identity and answers with a pong.
    /// </summary>
    [Fact]
    public void Handle_PingWithoutCaller_ReturnsPong()
    {
        var service = CreateService();

        var response = service.Handle(new(HelperPipeProtocol.OpPing, null, null), caller: null);

        Assert.True(response.Success);
        Assert.Equal("pong", response.Message);
    }

    /// <summary>
    /// Publish and unpublish are refused when the caller could not be identified.
    /// </summary>
    /// <param name="op">The operation.</param>
    [Theory]
    [InlineData(HelperPipeProtocol.OpPublish)]
    [InlineData(HelperPipeProtocol.OpUnpublish)]
    public void Handle_ChangeWithoutCaller_IsRefused(string op)
    {
        var service = CreateService();

        var response = service.Handle(new(op, "R", @"\Device\X"), caller: null);

        Assert.False(response.Success);
        Assert.Equal("Could not identify the calling user.", response.Message);
    }

    /// <summary>
    /// A caller outside the Administrators group is refused when the setting is not enabled.
    /// </summary>
    [Fact]
    public void Handle_PublishFromNonAdministrator_IsRefused()
    {
        var service = CreateService();
        var caller = new PipeClientIdentity("S-1-5-21-1-2-3-1001", ["S-1-5-32-545"]);

        var response = service.Handle(new(HelperPipeProtocol.OpPublish, "R", @"\Device\X"), caller);

        Assert.False(response.Success);
        Assert.Equal(HelperPipeService.AdministratorsOnlyMessage, response.Message);
    }

    /// <summary>
    /// An administrator's publish without a letter or device path is rejected before it reaches the mount manager.
    /// </summary>
    /// <param name="letter">The letter, if any.</param>
    /// <param name="devicePath">The device path, if any.</param>
    [Theory]
    [InlineData(null, @"\Device\X")]
    [InlineData("R", null)]
    public void Handle_PublishMissingArguments_ReturnsError(string? letter, string? devicePath)
    {
        var service = CreateService();

        var response = service.Handle(new(HelperPipeProtocol.OpPublish, letter, devicePath), AdministratorCaller);

        Assert.False(response.Success);
        Assert.Equal("publish requires Letter and DevicePath.", response.Message);
    }

    /// <summary>
    /// An administrator's unpublish without a letter is rejected.
    /// </summary>
    [Fact]
    public void Handle_UnpublishMissingLetter_ReturnsError()
    {
        var service = CreateService();

        var response = service.Handle(new(HelperPipeProtocol.OpUnpublish, null, null), AdministratorCaller);

        Assert.False(response.Success);
        Assert.Equal("unpublish requires Letter.", response.Message);
    }

    /// <summary>
    /// An unknown operation is reported back instead of being ignored.
    /// </summary>
    [Fact]
    public void Handle_UnknownOp_ReturnsError()
    {
        var service = CreateService();

        var response = service.Handle(new("frobnicate", null, null), caller: null);

        Assert.False(response.Success);
        Assert.Equal("Unknown op 'frobnicate'.", response.Message);
    }

    /// <summary>
    /// A caller who belongs to the Administrators group.
    /// </summary>
    private static PipeClientIdentity AdministratorCaller { get; } =
        new("S-1-5-21-1-2-3-500", ["S-1-5-32-544"]);

    /// <summary>
    /// Creates a service whose mount manager is never reached by the requests these tests send.
    /// </summary>
    /// <returns>The service.</returns>
    private static HelperPipeService CreateService() =>
        new(new GlobalMountManager(NullLogger<GlobalMountManager>.Instance), NullLogger<HelperPipeService>.Instance);
}
