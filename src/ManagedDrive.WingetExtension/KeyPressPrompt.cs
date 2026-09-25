namespace ManagedDrive.WingetExtension;

/// <summary>
/// Stands in for winget's <c>--wait</c> prompt when wingetx completes a request itself, so the
/// wait happens after the installer finishes rather than between the download and the install.
/// </summary>
internal static class KeyPressPrompt
{
    /// <summary>
    /// Waits for a key press on the console, unless there is no console key to read.
    /// </summary>
    public static void Wait() =>
        Wait(Console.IsInputRedirected, Console.Out, () => Console.ReadKey(intercept: true));

    /// <summary>
    /// Prompts on <paramref name="output"/> and waits for <paramref name="readKey"/>, unless
    /// standard input is redirected and there is no key to read. A process without a console
    /// (e.g. one started detached) also has nothing to read a key from, which
    /// <paramref name="readKey"/> reports by throwing <see cref="InvalidOperationException"/>;
    /// the request has already been handled by then, so that just ends the wait.
    /// </summary>
    /// <param name="isInputRedirected">Whether standard input is redirected.</param>
    /// <param name="output">Where to write the prompt.</param>
    /// <param name="readKey">Blocks until a key is pressed.</param>
    internal static void Wait(bool isInputRedirected, TextWriter output, Action readKey)
    {
        if (isInputRedirected)
        {
            return;
        }

        output.WriteLine("Press any key to continue...");
        try
        {
            readKey();
        }
        catch (InvalidOperationException)
        {
            // No console to read a key from; nothing to wait for.
        }
    }
}
