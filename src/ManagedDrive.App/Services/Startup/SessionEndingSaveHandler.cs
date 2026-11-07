using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ManagedDrive.App.Services;

/// <summary>
/// Handles Windows logoff/shutdown/restart: when Windows asks to end the session
/// (<see cref="SystemEvents.SessionEnding"/>) it saves every mounted disk's image without
/// unmounting, within a bounded time budget; when the end is confirmed
/// (<see cref="SystemEvents.SessionEnded"/>) it also puts the environment back and saves what
/// changed since. Extracted from <see cref="App"/>.
/// </summary>
public sealed class SessionEndingSaveHandler
{
    /// <summary>
    /// Default upper bound on how long <see cref="SaveAllDisks"/> waits for all disk saves to finish.
    /// Generous because a multi-gigabyte image takes well over 10 s to write, and the shutdown block
    /// reason keeps Windows waiting meanwhile.
    /// </summary>
    public static readonly TimeSpan DefaultSaveTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a call that is not the final one waits for saves of an earlier call that are still
    /// running, i.e. a stuck save. Short, so the session-ending notifications do not each hold the
    /// shutdown for a full <see cref="DefaultSaveTimeout"/>; the final call (<see cref="OnSessionEnded"/>)
    /// is the last chance and waits the full timeout.
    /// </summary>
    public static readonly TimeSpan DefaultFollowUpTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Serializes <see cref="SaveAllDisks"/> so the WPF and <see cref="SystemEvents"/> notifications,
    /// which both fire on a session end, never save concurrently.
    /// </summary>
    private readonly Lock _saveLock = new();

    /// <summary>
    /// Upper bound on how long <see cref="SaveAllDisks"/> waits for all disk saves to finish.
    /// </summary>
    private readonly TimeSpan _saveTimeout;

    /// <summary>
    /// How long a non-final call waits for earlier saves that are still running.
    /// </summary>
    private readonly TimeSpan _followUpTimeout;

    /// <summary>
    /// Supplies the saves to run, one per mounted disk.
    /// </summary>
    private readonly Func<IReadOnlyList<Action>> _saveSource;

    /// <summary>
    /// The saves of the latest <see cref="SaveAllDisks"/> call that started any; guarded by
    /// <see cref="_saveLock"/>. While they are still running (the call timed out) a save holds its
    /// disk, so the next call waits for them instead of queuing another set behind them.
    /// </summary>
    private Task _previousSaves = Task.CompletedTask;

    /// <summary>
    /// Shares the shutdown block reason between overlapping calls and saves that outlast them.
    /// </summary>
    private readonly ShutdownBlockCounter _blocks;

    /// <summary>
    /// The window the block reason is registered for; set when it is registered.
    /// </summary>
    private IntPtr _blockHandle;

    private readonly Func<IntPtr> _mainWindowHandleProvider;
    private readonly ILogger<SessionEndingSaveHandler> _logger;

    /// <summary>
    /// Run once the session end is confirmed; puts back the environment variables that point into the disks.
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
    /// Optional step run on the <see cref="SystemEvents"/> thread when the session end is confirmed
    /// (<see cref="OnSessionEnded"/>), before the final save, used to restore the TEMP directory and
    /// the preset environment variables: the process may be killed right after, and a variable left
    /// pointing at a drive that is not mounted yet breaks every program that starts before this app
    /// at the next sign-in. Not run when Windows merely asks (another program can still veto the
    /// end, and the session would go on with its environment restored). A failure in it is logged
    /// and never stops the saves.
    /// </param>
    /// <param name="saveTimeout">
    /// How long to wait for the saves; <see cref="DefaultSaveTimeout"/> when <see langword="null"/>.
    /// </param>
    /// <param name="followUpTimeout">
    /// How long a non-final call waits for earlier running saves; <see cref="DefaultFollowUpTimeout"/> when
    /// <see langword="null"/>.
    /// </param>
    /// <param name="saveSource">
    /// Supplies the saves to run; the mounted disks' <c>SaveToImageSafe</c> when <see langword="null"/>.
    /// A seam for tests, which cannot mount real disks.
    /// </param>
    /// <param name="blocks">
    /// The shutdown block reason to hold while saving; one backed by the main window when
    /// <see langword="null"/>. A seam for tests, which cannot observe the native calls.
    /// </param>
    public SessionEndingSaveHandler(
        MountManager mountManager,
        Func<IntPtr> mainWindowHandleProvider,
        ILogger<SessionEndingSaveHandler> logger,
        Action? beforeSave = null,
        TimeSpan? saveTimeout = null,
        TimeSpan? followUpTimeout = null,
        Func<IReadOnlyList<Action>>? saveSource = null,
        ShutdownBlockCounter? blocks = null)
    {
        _saveTimeout = saveTimeout ?? DefaultSaveTimeout;
        _followUpTimeout = followUpTimeout ?? DefaultFollowUpTimeout;
        _saveSource = saveSource ?? (() => mountManager.GetAll().Select(disk => (Action)disk.SaveToImageSafe).ToList());
        _beforeSave = beforeSave;
        _mainWindowHandleProvider = mainWindowHandleProvider;
        _logger = logger;
        _blocks = blocks ?? new(RegisterBlockReason, ClearBlockReason);
    }

    /// <summary>
    /// Fires when Windows asks to log off, shut down or restart (the <see cref="SystemEvents"/>
    /// notification). Only saves: the end can still be vetoed. See <see cref="SaveAllDisks"/>.
    /// Never throws: it runs on the <see cref="SystemEvents"/> thread, where an exception would
    /// take the process down in the middle of the logoff.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="e">The session-ending arguments.</param>
    public void OnSessionEnding(object sender, SessionEndingEventArgs e) => RunGuarded(() => SaveAllDisks());

    /// <summary>
    /// Fires when Windows has confirmed the end of the session (the <see cref="SystemEvents"/>
    /// notification): puts the environment back, then saves again what changed since the first
    /// save, which is cheap when nothing did. This is the last chance to save, so it waits for
    /// saves still running from earlier calls for the full timeout. Never throws, like
    /// <see cref="OnSessionEnding"/>.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="e">The session-ended arguments.</param>
    public void OnSessionEnded(object sender, SessionEndedEventArgs e)
    {
        RunBeforeSave();
        RunGuarded(() => SaveAllDisks(isFinal: true));
    }

    /// <summary>
    /// Saves the disks unless the session end has already been cancelled by another handler (WPF's
    /// own notification), in which case there is nothing to prepare for.
    /// </summary>
    /// <param name="cancelled">Whether the session end is already cancelled.</param>
    public void SaveUnlessCancelled(bool cancelled)
    {
        if (!cancelled)
        {
            SaveAllDisks();
        }
    }

    /// <summary>
    /// Saves every mounted disk's image without unmounting, within the save timeout. WPF's own
    /// session-ending notification and the <see cref="SystemEvents"/> ones all call this, and the
    /// OS may kill the process shortly after, so it must finish before they return. Disks are
    /// saved in parallel — each save is independent file I/O guarded by its own <c>RamDisk</c>
    /// lock — so the total fits the budget even with several large/compressed/encrypted disks.
    /// Blocks until all saves finish or the timeout elapses, so a stuck save (e.g. an unreachable
    /// backing path) cannot hang the callback. Not a one-shot: these notifications arrive when
    /// Windows asks to end the session, which another program can still veto, so a later call
    /// must save again. A disk that has not changed since its last save is skipped by
    /// <c>SaveToImageSafe</c>, which makes a repeated call cheap; concurrent calls take turns.
    /// When saves of an earlier call are still running, no new set is queued behind them: the call
    /// waits for them (only <see cref="DefaultFollowUpTimeout"/> unless it is the final one), and
    /// saves again once they are done. The final call shares one save timeout between waiting for
    /// the earlier saves and its own (but gives its own at least the follow-up timeout), so it
    /// never holds the shutdown for twice the timeout. The list of disks is taken only after that
    /// wait, so a disk unmounted or mounted meanwhile is handled correctly. The shutdown block
    /// reason stays registered while saves are still running and is cleared when the last of them
    /// finishes, or with the process.
    /// </summary>
    /// <param name="isFinal">
    /// Whether this is the last chance to save (the session end is confirmed), so earlier running
    /// saves are waited for the full timeout.
    /// </param>
    public void SaveAllDisks(bool isFinal = false)
    {
        lock (_saveLock)
        {
            // Tell Windows we are busy saving so it does not force-kill us before the saves
            // finish. Without this, the OS honours only its ~5 s HungAppTimeout, which the
            // save can exceed for large/compressed/encrypted disks.
            var block = _blocks.Acquire();

            // The saves this call is responsible for the block of: the earlier ones while it waits
            // for them, then its own.
            var pending = _previousSaves;
            var started = Stopwatch.GetTimestamp();
            try
            {
                if (!pending.IsCompleted &&
                    !WaitFor(pending, isFinal ? _saveTimeout : _followUpTimeout))
                {
                    _logger.LogWarning("Session-ending save: an earlier save is still running; not starting another.");
                    return;
                }

                // After the wait: a disk unmounted or mounted meanwhile must not be missed or touched stale.
                var saves = _saveSource();
                _logger.LogInformation("Session ending; saving {DiskCount} mounted disk(s).", saves.Count);

                pending = StartSaves(saves, ex => _logger.LogWarning(ex, "A disk save failed during session ending."));
                _previousSaves = pending;
                var ownTimeout = GetOwnSaveTimeout(isFinal, started);
                if (WaitFor(pending, ownTimeout))
                {
                    _logger.LogInformation("Session-ending save completed for all mounted disks.");
                }
                else
                {
                    _logger.LogWarning("Session-ending save timed out after {Timeout} waiting for disk saves to complete.", ownTimeout);
                }
            }
            finally
            {
                if (pending.IsCompleted)
                {
                    block.Dispose();
                }
                else
                {
                    _ = pending.ContinueWith(_ => block.Dispose(), TaskScheduler.Default);
                }
            }
        }
    }

    /// <summary>
    /// How long a call waits for its own set of saves. A non-final call gets the full timeout; the
    /// final call gets what is left of one timeout after waiting for earlier saves, but at least
    /// the follow-up timeout.
    /// </summary>
    /// <param name="isFinal">Whether this is the final call.</param>
    /// <param name="started">The <see cref="Stopwatch.GetTimestamp"/> value when the call began waiting.</param>
    /// <returns>The time to wait for the call's own saves.</returns>
    private TimeSpan GetOwnSaveTimeout(bool isFinal, long started)
    {
        if (!isFinal)
        {
            return _saveTimeout;
        }

        var remaining = _saveTimeout - Stopwatch.GetElapsedTime(started);
        return remaining > _followUpTimeout ? remaining : _followUpTimeout;
    }

    /// <summary>
    /// Starts the given saves in parallel on the thread pool.
    /// </summary>
    /// <param name="saves">The saves to run.</param>
    /// <param name="onFailure">
    /// Receives the exception of a save that threw; it does not stop the others. If it throws itself,
    /// the returned task faults.
    /// </param>
    /// <returns>A task that completes when every save has finished or failed.</returns>
    public static Task StartSaves(IEnumerable<Action> saves, Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onFailure);

        return Task.WhenAll(saves.Select(save => Task.Run(() =>
        {
            try
            {
                save();
            }
            catch (Exception ex)
            {
                // Best-effort, matches RamDisk.Dispose()/TryAutoSave() swallow pattern.
                onFailure(ex);
            }
        })));
    }

    /// <summary>
    /// Waits for the saves, at most <paramref name="timeout"/>. A failure of the failure callback
    /// itself is logged and counts as finished, since the saves are over.
    /// </summary>
    /// <param name="all">The task returned by <see cref="StartSaves"/>.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns><see langword="true"/> when the saves finished within the timeout.</returns>
    private bool WaitFor(Task all, TimeSpan timeout)
    {
        try
        {
            return all.Wait(timeout);
        }
        catch (AggregateException ex)
        {
            _logger.LogError(ex.Flatten(), "Reporting a failed disk save during session ending failed.");
            return true;
        }
    }

    /// <summary>
    /// Registers the shutdown block reason for the main window. The window handle must still exist,
    /// so this only works while the main window is alive, i.e. during the session-ending
    /// notifications and not once the application has exited.
    /// </summary>
    /// <returns>Whether the reason is now registered.</returns>
    private bool RegisterBlockReason()
    {
        var handle = _mainWindowHandleProvider();
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (!ShutdownBlockReasonCreate(handle, Loc.Get("Shutdown.SavingReason")))
        {
            _logger.LogWarning(
                "Could not register the shutdown block reason (Win32 error {Error}).", Marshal.GetLastWin32Error());
            return false;
        }

        _blockHandle = handle;
        return true;
    }

    /// <summary>
    /// Clears the shutdown block reason registered by <see cref="RegisterBlockReason"/>.
    /// </summary>
    private void ClearBlockReason() => ShutdownBlockReasonDestroy(_blockHandle);

    /// <summary>
    /// Runs a notification body, logging instead of throwing.
    /// </summary>
    /// <param name="body">The work to run.</param>
    private void RunGuarded(Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the disks while the session ends failed.");
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
