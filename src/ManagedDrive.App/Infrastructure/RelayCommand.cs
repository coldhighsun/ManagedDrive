using System.Windows.Input;

namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// A lightweight <see cref="ICommand"/> implementation that delegates execution and
/// can-execute logic to caller-supplied delegates.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<object?> _execute;

    /// <summary>
    /// Initializes a new <see cref="RelayCommand"/>.
    /// </summary>
    /// <param name="execute">The delegate invoked by <see cref="Execute"/>.</param>
    /// <param name="canExecute">
    /// Optional delegate invoked by <see cref="CanExecute"/>. When <c>null</c> the command
    /// is always executable.
    /// </param>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

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
    /// Raises <see cref="CanExecuteChanged"/> to force WPF to re-evaluate all bindings.
    /// </summary>
    public void Refresh() => CommandManager.InvalidateRequerySuggested();
}