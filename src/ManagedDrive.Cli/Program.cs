using ManagedDrive.Cli.Core;
using System.Diagnostics;

namespace ManagedDrive.Cli;

/// <summary>
/// Console-subsystem entry point for the <c>mdrive</c> CLI. Unlike <c>ManagedDrive.exe</c>
/// (a <c>WinExe</c>), this is a real console-subsystem executable, so the invoking shell
/// naturally blocks until it exits — CLI output can never race with the shell prompt returning.
/// </summary>
public static class Program
{
    /// <summary>
    /// How long to keep retrying the connection after launching the app, or while an already
    /// running instance is busy serving another connection.
    /// </summary>
    private static readonly TimeSpan LaunchWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Delay between connection attempts while waiting.
    /// </summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(200);

    // Relative path arguments need no rewriting here: CliPipeClient sends this process's working
    // directory along with the args, and the app resolves paths against it.
    public static async Task<int> Main(string[] args)
    {
        // File names and volume labels can hold any Unicode character, which the console's
        // legacy code page would print as '?'.
        ConsoleUnicodeOutput.Enable();

        if (CliPipeClient.TrySend(args, out var response))
        {
            return CliOutputRenderer.Render(response);
        }

        // The request was not delivered. Either no instance is running, or one is but didn't
        // accept the connection in time — its pipe serves one connection at a time, so a long
        // command from another mdrive keeps it busy. Launching the exe in the latter case would
        // only pop the second instance's "already running" dialog, so just wait for it instead.
        var alreadyRunning = AppInstance.IsRunning();
        if (!alreadyRunning && !TryLaunchApp())
        {
            await Console.Error.WriteLineAsync("Could not find or start ManagedDrive.exe.");
            return 1;
        }

        await Task.Delay(RetryInterval);
        if (await CliPipeClient.SendWithRetryAsync(args, LaunchWaitTimeout, RetryInterval) is { } delivered)
        {
            return CliOutputRenderer.Render(delivered);
        }

        await Console.Error.WriteLineAsync(alreadyRunning
            ? "Timed out waiting for ManagedDrive to accept the command; it may be busy with another one."
            : "Timed out waiting for ManagedDrive to start.");
        return 1;
    }

    private static bool TryLaunchApp()
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "ManagedDrive.exe");
        if (!File.Exists(exePath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
