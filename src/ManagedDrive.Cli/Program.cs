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

    /// <summary>
    /// Relative path arguments need no rewriting here: CliPipeClient sends this process's working
    /// directory along with the args, and the app resolves paths against it.
    /// Runs the CLI: forwards the arguments to the running app and returns the process exit code.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        // File names and volume labels can hold any Unicode character, which the console's
        // legacy code page would print as '?'.
        ConsoleUnicodeOutput.Enable();

        if (CliCommandProcessor.IsWatchCommand(args, out _))
        {
            return await WatchAsync(args);
        }

        if (CliPipeClient.TrySend(args, out var response))
        {
            return CliOutputRenderer.Render(response);
        }

        // The request was not delivered. Either no instance is running, or one is but didn't
        // accept the connection in time — its pipe serves one connection at a time, so a long
        // command from another mdrive keeps it busy. Launching the exe in the latter case would
        // only pop the second instance's "already running" dialog, so just wait for it instead.
        var alreadyRunning = AppInstance.IsRunning();
        if (!alreadyRunning && CliCommandProcessor.IsExitCommand(args))
        {
            // Starting the whole GUI only to tell it to exit again would flash a tray icon and,
            // if the exit races the startup, leave the app running.
            await Console.Out.WriteLineAsync("ManagedDrive is not running.");
            return 0;
        }

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

    /// <summary>
    /// Runs <c>mdrive watch</c>: connects to the running app (starting it if needed) and prints each
    /// event line the app streams back, until Ctrl+C or the app goes away.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>0 after Ctrl+C; 1 if the app refused the watch, closed it, or could not be reached.</returns>
    private static async Task<int> WatchAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Let the watch wind down and return normally instead of killing the process.
            e.Cancel = true;
            cts.Cancel();
        };

        var exitCode = 0;
        var printedFailure = false;
        void OnResponse(CliResponse response)
        {
            if (response.Success)
            {
                Console.WriteLine(response.Message);
                return;
            }

            printedFailure = true;
            exitCode = response.ExitCode;
            Console.Error.WriteLine(response.Message);
        }

        var end = await CliPipeClient.WatchAsync(args, OnResponse, cts.Token);
        if (end == CliPipeClient.WatchEnd.NotDelivered)
        {
            if (!AppInstance.IsRunning() && !TryLaunchApp())
            {
                await Console.Error.WriteLineAsync("Could not find or start ManagedDrive.exe.");
                return 1;
            }

            var waited = Stopwatch.StartNew();
            while (end == CliPipeClient.WatchEnd.NotDelivered && waited.Elapsed < LaunchWaitTimeout && !cts.IsCancellationRequested)
            {
                await Task.Delay(RetryInterval);
                end = await CliPipeClient.WatchAsync(args, OnResponse, cts.Token);
            }

            if (end == CliPipeClient.WatchEnd.NotDelivered)
            {
                await Console.Error.WriteLineAsync("Timed out waiting for ManagedDrive to accept the command; it may be busy with another one.");
                return 1;
            }
        }

        if (end == CliPipeClient.WatchEnd.Closed && !printedFailure)
        {
            await Console.Error.WriteLineAsync("ManagedDrive stopped sending events.");
            return 1;
        }

        return end == CliPipeClient.WatchEnd.Cancelled ? 0 : exitCode;
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
            // ShellExecute launches the long-lived GUI app without inheriting this process's
            // standard handles: otherwise `mdrive list | more` or `$x = mdrive list` would never
            // see EOF because the app keeps the pipe's write end open. The explicit working
            // directory stops the app from pinning the caller's current directory.
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
