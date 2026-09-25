using System.Diagnostics.CodeAnalysis;

namespace ManagedDrive.WingetExtension;

/// <summary>
/// Classifies the arguments of a <c>winget install</c>/<c>winget upgrade</c> request for
/// <see cref="SilentInstaller"/>, which replaces it with <c>winget download</c> plus a direct
/// installer launch. Only arguments with the same meaning for all three commands are forwarded to
/// <c>winget download</c>; <c>-h</c>/<c>--silent</c> (which only picks the installer's silent
/// switches) and <c>--wait</c> (honored after the install) are consumed. Any other install/upgrade-only option (<c>--interactive</c>,
/// <c>--override</c>, <c>--location</c>, <c>--log</c>, <c>--all</c>, ...) can't be honored by a
/// direct installer launch, so the request is left to plain winget instead of being dropped.
/// </summary>
internal static class WingetInstallArguments
{
    /// <summary>
    /// Asks winget to wait for a key press before exiting. Consumed rather than forwarded, since
    /// <c>winget download</c> would wait between the download and the install instead of after
    /// the install.
    /// </summary>
    private const string WaitFlag = "--wait";

    /// <summary>
    /// Options that take a value and are accepted by <c>install</c>, <c>upgrade</c> and
    /// <c>download</c> alike.
    /// </summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "-q", "--query", "-m", "--manifest", "--id", "--name", "--moniker", "-v", "--version",
        "-s", "--source", "--scope", "-a", "--architecture", "--installer-type", "--locale",
        "--header", "--authentication-mode", "--authentication-account", "--proxy",
    };

    /// <summary>
    /// Flags accepted by <c>install</c>, <c>upgrade</c> and <c>download</c> alike.
    /// </summary>
    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal)
    {
        "-e", "--exact", "--ignore-security-hash", "--skip-dependencies",
        "--accept-package-agreements", "--accept-source-agreements", "--logs", "--open-logs",
        "--verbose", "--verbose-logs", "--nowarn", "--ignore-warnings",
        "--disable-interactivity", "--no-proxy",
    };

    /// <summary>
    /// Value options that name the package (or its manifest) to install.
    /// </summary>
    private static readonly HashSet<string> SelectorOptions = new(StringComparer.Ordinal)
    {
        "-q", "--query", "-m", "--manifest", "--id", "--name", "--moniker",
    };

    /// <summary>
    /// Selectors that each add a query; <c>winget install</c> accepts several (one package each),
    /// but <c>winget download</c> only one.
    /// </summary>
    private static readonly HashSet<string> QueryOptions = new(StringComparer.Ordinal) { "-q", "--query" };

    /// <summary>
    /// Flags that ask for a fully silent install; consumed rather than forwarded, since
    /// <c>winget download</c> rejects them.
    /// </summary>
    private static readonly HashSet<string> SilentFlags = new(StringComparer.Ordinal) { "-h", "--silent" };

    /// <summary>
    /// Tries to turn the arguments following <c>install</c>/<c>upgrade</c> into a
    /// <c>winget download</c> request for exactly one package.
    /// </summary>
    /// <param name="args">The arguments after the subcommand.</param>
    /// <param name="parsed">The download arguments and install mode, when this returns <c>true</c>.</param>
    /// <returns>
    /// <c>false</c> if the request doesn't name exactly one package (e.g. <c>upgrade --all</c>,
    /// several packages, or none), has a malformed or unknown argument, or uses an option only a
    /// real <c>winget install</c>/<c>upgrade</c> can honor; the caller should forward the request
    /// to winget unchanged.
    /// </returns>
    public static bool TryParse(IReadOnlyList<string> args, [NotNullWhen(true)] out ParsedInstallArguments? parsed)
    {
        parsed = null;
        var downloadArgs = new List<string>(args.Count);
        var useFullSilent = false;
        var waitForKeyPress = false;
        var hasSelector = false;
        var queryCount = 0;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
            {
                // A positional argument is a query.
                hasSelector = true;
                queryCount++;
                downloadArgs.Add(arg);
                continue;
            }

            var separator = arg.IndexOf('=');
            var name = separator < 0 ? arg : arg[..separator];

            if (ValueOptions.Contains(name))
            {
                downloadArgs.Add(arg);
                if (separator < 0)
                {
                    if (i + 1 >= args.Count)
                    {
                        return false;
                    }

                    downloadArgs.Add(args[++i]);
                }

                hasSelector |= SelectorOptions.Contains(name);
                queryCount += QueryOptions.Contains(name) ? 1 : 0;
                continue;
            }

            if (separator >= 0)
            {
                return false;
            }

            if (SilentFlags.Contains(arg))
            {
                useFullSilent = true;
                continue;
            }

            if (arg == WaitFlag)
            {
                waitForKeyPress = true;
                continue;
            }

            if (!FlagOptions.Contains(arg))
            {
                return false;
            }

            useFullSilent |= arg == "--disable-interactivity";
            downloadArgs.Add(arg);
        }

        if (!hasSelector || queryCount > 1)
        {
            return false;
        }

        parsed = new(downloadArgs, useFullSilent, waitForKeyPress);
        return true;
    }
}

/// <summary>
/// The result of <see cref="WingetInstallArguments.TryParse"/>.
/// </summary>
/// <param name="DownloadArgs">The arguments to pass on to <c>winget download</c>.</param>
/// <param name="UseFullSilent">
/// Whether the installer should run fully silent (<c>--silent</c> or
/// <c>--disable-interactivity</c>) rather than silent with progress.
/// </param>
/// <param name="WaitForKeyPress">
/// Whether to wait for a key press once the install finishes (<c>--wait</c>).
/// </param>
internal sealed record ParsedInstallArguments(IReadOnlyList<string> DownloadArgs, bool UseFullSilent, bool WaitForKeyPress);
