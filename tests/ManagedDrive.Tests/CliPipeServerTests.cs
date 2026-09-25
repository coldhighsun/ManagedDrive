using System.IO.Pipes;
using System.Security.Principal;
using ManagedDrive.App.Cli;
using ManagedDrive.HelperProtocol;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the CLI pipe server's pipe security.
/// </summary>
public sealed class CliPipeServerTests
{
    /// <summary>
    /// Pipe name unique to this test.
    /// </summary>
    private readonly string _pipeName = $"ManagedDrive-Test-CliServer-{Guid.NewGuid()}";

    /// <summary>
    /// The pipe refuses to join a pipe another process already created under the name.
    /// </summary>
    [Fact]
    public void CreatePipe_NameAlreadyHeldByAnotherPipe_Throws()
    {
        // A squatter's pipe that would accept further instances — joining it must still fail.
        using var squatter = new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Assert.Throws<UnauthorizedAccessException>(() => CliPipeServer.CreatePipe(_pipeName));
    }

    /// <summary>
    /// The pipe admits its creator and is owned by the creator's token owner, which the client checks.
    /// </summary>
    [Fact]
    public async Task CreatePipe_FreeName_AcceptsThisUserAndIsOwnedByItsTokenOwner()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var server = CliPipeServer.CreatePipe(_pipeName);
        var connectTask = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await client.ConnectAsync(TestContext.Current.CancellationToken);
        await connectTask;

        Assert.Equal(identity.Owner, PipeSecurityRules.TryGetOwner(client));
    }
}
