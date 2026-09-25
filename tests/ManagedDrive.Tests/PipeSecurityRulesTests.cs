using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ManagedDrive.HelperProtocol;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the security rules shared by the solution's named pipes.
/// </summary>
public sealed class PipeSecurityRulesTests
{
    /// <summary>
    /// SID standing in for the calling user.
    /// </summary>
    private static readonly SecurityIdentifier User = new("S-1-5-21-1000000000-2000000000-3000000000-1001");

    /// <summary>
    /// SID standing in for a different user.
    /// </summary>
    private static readonly SecurityIdentifier OtherUser = new("S-1-5-21-1000000000-2000000000-3000000000-1002");

    /// <summary>
    /// A pipe the trusted user created is trusted.
    /// </summary>
    [Fact]
    public void IsTrustedOwner_OwnerIsTrustedUser_ReturnsTrue()
    {
        Assert.True(PipeSecurityRules.IsTrustedOwner(User, User));
    }

    /// <summary>
    /// A pipe another unprivileged user created is not trusted.
    /// </summary>
    [Fact]
    public void IsTrustedOwner_OwnerIsAnotherUser_ReturnsFalse()
    {
        Assert.False(PipeSecurityRules.IsTrustedOwner(OtherUser, User));
    }

    /// <summary>
    /// A pipe an elevated process created is trusted: only a privileged creator can make Administrators the owner.
    /// </summary>
    [Fact]
    public void IsTrustedOwner_OwnerIsAdministrators_ReturnsTrue()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        Assert.True(PipeSecurityRules.IsTrustedOwner(administrators, User));
    }

    /// <summary>
    /// A pipe LocalSystem created is trusted even with no trusted user.
    /// </summary>
    [Fact]
    public void IsTrustedOwner_OwnerIsLocalSystemWithNoTrustedUser_ReturnsTrue()
    {
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        Assert.True(PipeSecurityRules.IsTrustedOwner(localSystem, null));
    }

    /// <summary>
    /// With no trusted user, only a privileged owner is trusted.
    /// </summary>
    [Fact]
    public void IsTrustedOwner_UnprivilegedOwnerWithNoTrustedUser_ReturnsFalse()
    {
        Assert.False(PipeSecurityRules.IsTrustedOwner(User, null));
    }

    /// <summary>
    /// A pipe whose owner couldn't be read is not trusted.
    /// </summary>
    [Fact]
    public void IsTrustedOwner_OwnerUnknown_ReturnsFalse()
    {
        Assert.False(PipeSecurityRules.IsTrustedOwner(null, User));
    }

    /// <summary>
    /// The client end of a connection reads the owner the server created the pipe with.
    /// </summary>
    [Fact]
    public async Task TryGetOwner_ConnectedPipeWithExplicitOwner_ReturnsThatOwner()
    {
        var pipeName = $"ManagedDrive-Test-Owner-{Guid.NewGuid()}";
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetOwner(identity.User!);

        using var server = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        var connectTask = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        await connectTask;

        var owner = PipeSecurityRules.TryGetOwner(client);

        Assert.Equal(identity.User, owner);
    }
}
