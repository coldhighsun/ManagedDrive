using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public sealed class CliExitCommandTests
{
    [Theory]
    [InlineData("exit")]
    [InlineData("EXIT")]
    public void IsExitCommand_ExitAlone_ReturnsTrue(string arg)
    {
        Assert.True(CliCommandProcessor.IsExitCommand([arg]));
    }

    [Theory]
    [InlineData]
    [InlineData("list")]
    [InlineData("exit", "--help")]
    public void IsExitCommand_OtherArguments_ReturnsFalse(params string[] args)
    {
        Assert.False(CliCommandProcessor.IsExitCommand(args));
    }
}
