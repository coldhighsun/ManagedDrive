using ManagedDrive.WingetExtension;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="WingetInstallArguments"/>.
/// </summary>
public sealed class WingetInstallArgumentsTests
{
    /// <summary>
    /// <c>--silent</c>/<c>-h</c> (which <c>winget download</c> rejects) is consumed and selects a
    /// fully silent install; everything else is forwarded unchanged.
    /// </summary>
    /// <param name="silentFlag">The silent flag passed to <c>install</c>.</param>
    [Theory]
    [InlineData("--silent")]
    [InlineData("-h")]
    public void TryParse_SilentFlag_ConsumesItAndUsesFullSilent(string silentFlag)
    {
        var parsed = Parse("--id", "Git.Git", silentFlag, "-e");

        Assert.Equal(["--id", "Git.Git", "-e"], parsed.DownloadArgs);
        Assert.True(parsed.UseFullSilent);
    }

    /// <summary>
    /// <c>--disable-interactivity</c> also selects a fully silent install, and is forwarded since
    /// <c>winget download</c> accepts it.
    /// </summary>
    [Fact]
    public void TryParse_DisableInteractivity_ForwardsItAndUsesFullSilent()
    {
        var parsed = Parse("Git.Git", "--disable-interactivity");

        Assert.Equal(["Git.Git", "--disable-interactivity"], parsed.DownloadArgs);
        Assert.True(parsed.UseFullSilent);
    }

    /// <summary>
    /// Without a silent flag the installer runs silent with progress.
    /// </summary>
    [Fact]
    public void TryParse_NoSilentFlag_UsesSilentWithProgress()
    {
        var parsed = Parse("--id", "Git.Git");

        Assert.False(parsed.UseFullSilent);
    }

    /// <summary>
    /// <c>--wait</c> is consumed and honored after the install, since forwarding it to
    /// <c>winget download</c> would make it wait before the installer runs.
    /// </summary>
    [Fact]
    public void TryParse_WaitFlag_ConsumesItAndWaitsForKeyPress()
    {
        var parsed = Parse("--id", "Git.Git", "--wait");

        Assert.Equal(["--id", "Git.Git"], parsed.DownloadArgs);
        Assert.True(parsed.WaitForKeyPress);
    }

    /// <summary>
    /// Without <c>--wait</c> nothing waits for a key press.
    /// </summary>
    [Fact]
    public void TryParse_NoWaitFlag_DoesNotWaitForKeyPress()
    {
        var parsed = Parse("--id", "Git.Git");

        Assert.False(parsed.WaitForKeyPress);
    }

    /// <summary>
    /// Any single package selector (a positional query, or a query, id, name, moniker or manifest
    /// option, in either the separate or the <c>=</c> form) is accepted.
    /// </summary>
    /// <param name="args">The arguments after <c>install</c>.</param>
    [Theory]
    [InlineData("vscode")]
    [InlineData("-q", "vscode")]
    [InlineData("--id", "Microsoft.VisualStudioCode")]
    [InlineData("--id=Microsoft.VisualStudioCode")]
    [InlineData("--name", "Visual Studio Code")]
    [InlineData("--moniker", "vscode")]
    [InlineData("-m", @"C:\manifests\vscode")]
    public void TryParse_OnePackageSelector_ForwardsItVerbatim(params string[] args)
    {
        var parsed = Parse(args);

        Assert.Equal(args, parsed.DownloadArgs);
    }

    /// <summary>
    /// An option's value is not mistaken for a package query: <c>--source winget</c> alone names
    /// no package.
    /// </summary>
    [Fact]
    public void TryParse_OnlyOptionValues_ReturnsFalse()
    {
        var success = WingetInstallArguments.TryParse(["--source", "winget", "-v", "1.0"], out _);

        Assert.False(success);
    }

    /// <summary>
    /// Values of download-compatible options are forwarded along with their option, and don't
    /// count as extra queries.
    /// </summary>
    [Fact]
    public void TryParse_ValueOptionsWithQuery_ForwardsAllArguments()
    {
        var parsed = Parse("-s", "winget", "vscode", "--scope", "user", "-a", "x64", "--locale", "en-US");

        Assert.Equal(["-s", "winget", "vscode", "--scope", "user", "-a", "x64", "--locale", "en-US"], parsed.DownloadArgs);
    }

    /// <summary>
    /// Requests that don't name exactly one package are left to winget: none (e.g.
    /// <c>upgrade</c> with no arguments) or several, which <c>winget install</c> supports but
    /// <c>winget download</c> doesn't.
    /// </summary>
    /// <param name="args">The arguments after <c>install</c>/<c>upgrade</c>.</param>
    [Theory]
    [InlineData]
    [InlineData("--accept-source-agreements")]
    [InlineData("vscode", "git")]
    [InlineData("vscode", "-q", "git")]
    public void TryParse_NotExactlyOnePackage_ReturnsFalse(params string[] args)
    {
        var success = WingetInstallArguments.TryParse(args, out _);

        Assert.False(success);
    }

    /// <summary>
    /// Options only a real install/upgrade can honor, options <c>winget download</c> would
    /// misinterpret, and unknown or malformed arguments leave the request to winget instead of
    /// being dropped.
    /// </summary>
    /// <param name="args">The arguments after <c>install</c>/<c>upgrade</c>.</param>
    [Theory]
    [InlineData("vscode", "--interactive")]
    [InlineData("vscode", "-i")]
    [InlineData("vscode", "--override", "/VERYSILENT")]
    [InlineData("vscode", "--custom", "/NORESTART")]
    [InlineData("vscode", "-l", @"D:\Apps")]
    [InlineData("vscode", "--log", @"C:\install.log")]
    [InlineData("vscode", "--force")]
    [InlineData("vscode", "--no-upgrade")]
    [InlineData("--all")]
    [InlineData("vscode", "-d", @"C:\downloads")]
    [InlineData("vscode", "--help")]
    [InlineData("vscode", "--unknown-option")]
    [InlineData("vscode", "--exact=true")]
    [InlineData("vscode", "--version")]
    public void TryParse_UnsupportedOrMalformedArgument_ReturnsFalse(params string[] args)
    {
        var success = WingetInstallArguments.TryParse(args, out _);

        Assert.False(success);
    }

    /// <summary>
    /// Parses arguments that are expected to be accepted.
    /// </summary>
    /// <param name="args">The arguments after <c>install</c>/<c>upgrade</c>.</param>
    /// <returns>The parsed arguments.</returns>
    private static ParsedInstallArguments Parse(params string[] args)
    {
        Assert.True(WingetInstallArguments.TryParse(args, out var parsed));
        return parsed;
    }
}
