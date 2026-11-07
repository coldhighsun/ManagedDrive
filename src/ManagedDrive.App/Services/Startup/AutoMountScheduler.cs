namespace ManagedDrive.App.Services;

/// <summary>
/// Orders the startup auto-mount: loads run a few at a time in the background, and each result
/// is finished on the calling (UI) thread as soon as its load completes, so a slow load doesn't
/// keep faster, already-mounted disks out of the list. A result that needs a password is held
/// back until every other one is finished, then handled in original order.
/// </summary>
public static class AutoMountScheduler
{
    /// <summary>
    /// Loads <paramref name="count"/> items with at most <paramref name="concurrency"/> loads
    /// running at once and finishes every result, in the order described on the type. Never
    /// abandons a load: a failing <paramref name="finish"/> is reported to
    /// <paramref name="onFinishError"/>, a faulted load to <paramref name="onLoadError"/>, and
    /// the remaining results are still finished.
    /// </summary>
    /// <typeparam name="T">The result of loading one item.</typeparam>
    /// <param name="count">How many items there are.</param>
    /// <param name="concurrency">The most loads allowed to run at the same time; at least 1.</param>
    /// <param name="load">
    /// Starts loading the item with the given index. Should report problems in its result
    /// rather than throw; if it does throw, the item is handed to <paramref name="onLoadError"/>
    /// and never reaches <paramref name="finish"/>.
    /// </param>
    /// <param name="needsPassword">Whether a load result must wait for the user to type a password.</param>
    /// <param name="finish">Finishes one result; called one at a time from the calling context.</param>
    /// <param name="onFinishError">Receives the index and exception of a failing <paramref name="finish"/>.</param>
    /// <param name="onLoadError">
    /// Receives the index and exception of a faulted <paramref name="load"/>; the caller must do
    /// whatever <paramref name="finish"/> would have done to account for that item.
    /// </param>
    /// <returns>A task completing when every result has been finished.</returns>
    public static async Task RunAsync<T>(
        int count,
        int concurrency,
        Func<int, Task<T>> load,
        Func<T, bool> needsPassword,
        Func<int, T, Task> finish,
        Action<int, Exception> onFinishError,
        Action<int, Exception> onLoadError)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);

        // Not disposed: loads may still be waiting on it if this method exits early, and a
        // SemaphoreSlim whose wait handle was never requested holds no unmanaged resource.
        var gate = new SemaphoreSlim(concurrency);
        var pending = Enumerable.Range(0, count).ToDictionary(i => LoadAsync(i), i => i);
        var deferred = new List<(int Index, T Result)>();

        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending.Keys);
            var index = pending[done];
            pending.Remove(done);

            T result;
            try
            {
                result = await done;
            }
            catch (Exception ex)
            {
                // A faulted load must not abandon the other disks.
                onLoadError(index, ex);
                continue;
            }

            if (needsPassword(result))
            {
                deferred.Add((index, result));
                continue;
            }

            await FinishSafelyAsync(index, result);
        }

        foreach (var (index, result) in deferred.OrderBy(d => d.Index))
        {
            await FinishSafelyAsync(index, result);
        }

        async Task<T> LoadAsync(int index)
        {
            await gate.WaitAsync();
            try
            {
                return await load(index);
            }
            finally
            {
                gate.Release();
            }
        }

        async Task FinishSafelyAsync(int index, T result)
        {
            try
            {
                await finish(index, result);
            }
            catch (Exception ex)
            {
                onFinishError(index, ex);
            }
        }
    }
}
