using System.Runtime.InteropServices;

namespace ManagedDrive.App.Services;

/// <summary>
/// Handles <see cref="SystemEvents.SessionEnding"/> (Windows logoff/shutdown/restart) by saving
/// every mounted disk's image without unmounting, within a bounded time budget. Extracted from
/// <see cref="App"/>.
/// </summary>
public sealed class SessionEndingSaveHandler
{
    /// <summary>
    /// Upper bound on how long <see cref="OnSessionEnding"/> waits for all disk saves to finish.
    /// </summary>
    private static readonly TimeSpan SessionEndingSaveTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<IntPtr> _mainWindowHandleProvider;
    private readonly ILogger<SessionEndingSaveHandler> _logger;
    private readonly MountManager _mountManager;

    /// <summary>
    /// Run before the disks are saved; puts back the environment variables that point into them.
    /// </summary>
    private readonly Action? _beforeSave;

    /// <param name="mountManager">Source of the disks to save.</param>
    /// <param name="mainWindowHandleProvider">
    /// Lazily supplies the main window's HWND (captured once on the UI thread at startup) so this
    /// handler — which runs on the <see cref="SystemEvents"/> thread — never touches WPF objects
    /// cross-thread. A provider (rather than a fixed value) tolerates construction before the
    /// handle is actually assigned.
    /// </param>
    /// <param name="logger">Logger for the save-on-session-ending flow.</param>
    /// <param name="beforeSave">
    /// Optional step run on the <see cref="SystemEvents"/> thread before any disk is saved, used to
    /// restore the TEMP directory and the preset environment variables: the process may be killed
    /// right after the save budget, and a variable left pointing at a drive that is not mounted yet
    /// breaks every program that starts before this app at the next sign-in. A failure in it is
    /// logged and never stops the saves.
    /// </param>
    public SessionEndingSaveHandler(
        MountManager mountManager, Func<IntPtr> mainWindowHandleProvider, ILogger<SessionEndingSaveHandler> logger, Action? beforeSave = null)
    {
        _beforeSave = beforeSave;
        _mountManager = mountManager;
        _mainWindowHandleProvider = mainWindowHandleProvider;
        _logger = logger;
    }

    /// <summary>
    /// Fires when Windows is logging off, shutting down, or restarting. WPF's own
    /// <c>Exit</c> event does not fire in this case, and the OS may kill the process shortly
    /// after this callback returns, so save every mounted disk's image and without unmounting
    /// (unmounting is unnecessary here and would risk exceeding the shutdown time budget).
    /// Disks are saved in parallel rather than sequentially — each disk's save is independent
    /// file I/O guarded by its own <c>RamDisk</c> lock, so running them concurrently lets the
    /// total time fit the shutdown budget even with several large/compressed/encrypted disks.
    /// This method blocks synchronously until all saves finish or <see cref="SessionEndingSaveTimeout"/>
    /// elapses, whichever comes first — a bound is needed so a single stuck save (e.g. a backing
    /// path that has become unreachable) can't hang this callback indefinitely.
    /// </summary>
    public void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        var disks = _mountManager.GetAll();
        _logger.LogInformation("OnSessionEnding invoked; saving {DiskCount} mounted disk(s).", disks.Count);

        var mainWindowHandle = _mainWindowHandleProvider();
        var hasHandle = mainWindowHandle != IntPtr.Zero;
        if (hasHandle)
        {
            // Tell Windows we are busy saving so it does not force-kill us before the save loop
            // below finishes. Without this, the OS honours only its ~5 s HungAppTimeout, which the
            // save can exceed for large/compressed/encrypted disks.
            ShutdownBlockReasonCreate(mainWindowHandle, Loc.Get("Shutdown.SavingReason"));
        }

        try
        {
            RunBeforeSave();

            var saveTasks = disks
                .Select(disk => Task.Run(() =>
                {
                    try
                    {
                        disk.SaveToImageSafe();
                    }
                    catch (Exception ex)
                    {
                        // Best-effort, matches RamDisk.Dispose()/TryAutoSave() swallow pattern.
                        _logger.LogWarning(ex, "Failed to save disk {MountPoint} during session ending.", disk.Options.MountPoint);
                    }
                }))
                .ToArray();

            if (!Task.WaitAll(saveTasks, SessionEndingSaveTimeout))
            {
                _logger.LogWarning("OnSessionEnding timed out after {Timeout} waiting for disk saves to complete.", SessionEndingSaveTimeout);
            }
            else
            {
                _logger.LogInformation("OnSessionEnding completed saving all mounted disks.");
            }
        }
        finally
        {
            if (hasHandle)
            {
                // Always clear the block reason so we never hold up the shutdown once saving is
                // done (or has timed out).
                ShutdownBlockReasonDestroy(mainWindowHandle);
            }
        }
    }

    /// <summary>
    /// Runs the optional step before the saves, logging instead of throwing.
    /// </summary>
    private void RunBeforeSave()
    {
        try
        {
            _beforeSave?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restoring the environment before the session ends failed.");
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
}
