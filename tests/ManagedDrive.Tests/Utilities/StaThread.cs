namespace ManagedDrive.Tests;

/// <summary>
/// Runs test code on a single-threaded apartment, which WPF elements such as a window require.
/// </summary>
internal static class StaThread
{
    /// <summary>
    /// Runs <paramref name="action"/> on a dedicated STA thread and rethrows any exception on the
    /// calling thread.
    /// </summary>
    /// <param name="action">The work to run.</param>
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("STA test body failed.", error);
        }
    }
}
