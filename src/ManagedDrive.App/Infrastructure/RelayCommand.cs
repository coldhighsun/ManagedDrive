using System.Windows.Input;

namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// A lightweight <see cref="ICommand"/> implementation that delegates execution and
/// can-execute logic to caller-supplied delegates.
/// </summary>
/// <remarks>
/// Initializes a new <see cref="RelayCommand"/>.
/// </remarks>
/// <param name="execute">The delegate invoked by <see cref="Execute"/>.</param>
/// <param name="canExecute">
/// Optional delegate invoked by <see cref="CanExecute"/>. When <c>null</c> the command
/// is always executable.
/// </param>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private readonly Func<object?, bool>? _canExecute = canExecute;
    private readonly Action<object?> _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) =>
        _canExecute == null || _canExecute(parameter);

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>
    /// Creates a command whose action is asynchronous. An <c>async void</c> handler would send any
    /// exception to the dispatcher's unhandled-exception handler; here it goes to
    /// <paramref name="onError"/> instead, so the command owns the failure.
    /// </summary>
    /// <param name="executeAsync">The asynchronous action; its exceptions are passed to <paramref name="onError"/>.</param>
    /// <param name="onError">
    /// Receives every exception <paramref name="executeAsync"/> throws, on the thread the command
    /// was executed on (the UI thread for a bound command). Cancellation is not reported.
    /// </param>
    /// <param name="canExecute">Optional can-execute delegate; when <c>null</c> the command is always executable.</param>
    /// <returns>The command.</returns>
    public static RelayCommand FromAsync(Func<object?, Task> executeAsync, Action<Exception> onError, Func<object?, bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
        ArgumentNullException.ThrowIfNull(onError);

        // Fire and forget by design: RunAsync never throws, so the discarded task cannot fault.
        return new(parameter => _ = RunAsync(executeAsync, parameter, onError), canExecute);
    }

    /// <summary>
    /// Runs <paramref name="executeAsync"/> and hands any exception to <paramref name="onError"/>.
    /// </summary>
    /// <param name="executeAsync">The asynchronous action.</param>
    /// <param name="parameter">The command parameter.</param>
    /// <param name="onError">Receives every exception except cancellation.</param>
    /// <returns>A task that completes when the action has finished and any failure was reported.</returns>
    internal static async Task RunAsync(Func<object?, Task> executeAsync, object? parameter, Action<Exception> onError)
    {
        try
        {
            await executeAsync(parameter);
        }
        catch (OperationCanceledException)
        {
            // A cancelled operation is not a failure; it reports itself where it was cancelled.
        }
        catch (Exception ex)
        {
            onError(ex);
        }
    }

    /// <summary>
    /// Raises <see cref="CanExecuteChanged"/> to force WPF to re-evaluate all bindings.
    /// </summary>
    public void Refresh() => CommandManager.InvalidateRequerySuggested();
}
