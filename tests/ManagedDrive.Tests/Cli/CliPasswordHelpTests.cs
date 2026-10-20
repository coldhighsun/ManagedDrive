using System.Text.RegularExpressions;
using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests that the CLI help states the length limit of passwords the commands encrypt with, and
/// does not state it for the one that unlocks an existing image.
/// </summary>
public sealed class CliPasswordHelpTests
{
    /// <summary>
    /// The help text the limit is expected to appear as, matching
    /// <see cref="CreateDiskOptionsBuilder"/>'s limits.
    /// </summary>
    private static readonly string LimitText =
        $"{CreateDiskOptionsBuilder.MinPasswordLength} to {CreateDiskOptionsBuilder.MaxPasswordLength} characters";

    /// <summary>
    /// Renders the help of <paramref name="command"/> with its line breaks and runs of spaces
    /// collapsed, so a phrase wrapped by the help layout can still be searched for.
    /// </summary>
    /// <param name="command">The command whose help to render.</param>
    /// <returns>The help text on one line.</returns>
    private static async Task<string> HelpAsync(string command)
    {
        var outcome = await CliCommandProcessor.ExecuteAsync([command, "--help"], new Controller());

        Assert.Equal(0, outcome.ExitCode);
        return Regex.Replace(outcome.Message, @"\s+", " ");
    }

    /// <summary>
    /// The limits written in the help are the ones the dialogs and the app-side check enforce.
    /// </summary>
    [Fact]
    public void NewPasswordLength_Constants_MatchTheCoreLimits()
    {
        Assert.Equal(CreateDiskOptionsBuilder.MinPasswordLength, CliCommandProcessor.MinNewPasswordLength);
        Assert.Equal(CreateDiskOptionsBuilder.MaxPasswordLength, CliCommandProcessor.MaxNewPasswordLength);
    }

    /// <summary>
    /// Every password option of <c>set-password</c>, <c>create</c> and <c>export</c> names the
    /// allowed length; each of them has two (<c>--password</c> and <c>--password-file</c>).
    /// </summary>
    [Theory]
    [InlineData("set-password")]
    [InlineData("create")]
    [InlineData("export")]
    public async Task Help_CommandsThatEncrypt_StateTheLengthLimitOnBothPasswordOptions(string command)
    {
        var help = await HelpAsync(command);

        Assert.Equal(2, Regex.Count(help, Regex.Escape(LimitText)));
    }

    /// <summary>
    /// <c>mount</c> only unlocks an existing image, whose password may predate the limit, so its
    /// help does not state one.
    /// </summary>
    [Fact]
    public async Task Help_Mount_DoesNotStateTheLengthLimit()
    {
        var help = await HelpAsync("mount");

        Assert.DoesNotContain(LimitText, help);
    }

    /// <summary>
    /// A controller that is never called; the help is rendered without it.
    /// </summary>
    private sealed class Controller : StubCliDiskController
    {
    }
}
