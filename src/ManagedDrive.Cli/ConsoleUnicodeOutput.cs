using System.Text;

namespace ManagedDrive.Cli;

/// <summary>
/// Makes console output lossless for file names and volume labels outside the console's code
/// page. With UTF-16 as the output encoding, .NET writes to the console through the wide console
/// API instead of transcoding to the code page, and doesn't change the code page itself — which
/// the console shares with the invoking shell, so there is nothing to restore even if
/// <c>mdrive</c> is killed with Ctrl+C.
/// </summary>
internal static class ConsoleUnicodeOutput
{
    /// <summary>
    /// Switches console output to UTF-16 if <see cref="ShouldEnable"/> says so, keeping a
    /// redirected standard error in its original encoding.
    /// </summary>
    public static void Enable()
    {
        if (!ShouldEnable(Console.IsOutputRedirected))
        {
            return;
        }

        // The output encoding applies to standard error too, and a redirected stream would then
        // receive raw UTF-16 bytes instead of text in the code page its consumer decodes with.
        // Creating the redirected writer before the switch gives it the original encoding;
        // reinstall it afterwards so it survives the switch.
        var redirectedError = Console.IsErrorRedirected ? Console.Error : null;

        Console.OutputEncoding = Encoding.Unicode;

        if (redirectedError != null)
        {
            Console.SetError(redirectedError);
        }
    }

    /// <summary>
    /// Returns whether console output should be switched to UTF-16: only when standard output,
    /// where all rendered disk and snapshot names go, is the console. A redirected standard
    /// output keeps its default encoding so its consumer can decode it; <c>list --json</c>
    /// escapes non-ASCII characters and is lossless either way.
    /// </summary>
    /// <param name="isOutputRedirected">Whether standard output is redirected.</param>
    /// <returns><c>true</c> if standard output is the console.</returns>
    internal static bool ShouldEnable(bool isOutputRedirected) => !isOutputRedirected;
}
