namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the asynchronous side of <see cref="RelayCommand"/>, which owns the failures of
/// commands that would otherwise be <c>async void</c> handlers.
/// </summary>
public sealed class RelayCommandTests
{
    /// <summary>
    /// A command that finishes normally never calls the error handler.
    /// </summary>
    [Fact]
    public async Task RunAsync_ActionSucceeds_DoesNotCallErrorHandler()
    {
        var errors = new List<Exception>();

        await RelayCommand.RunAsync(_ => Task.CompletedTask, parameter: null, errors.Add);

        Assert.Empty(errors);
    }

    /// <summary>
    /// An exception thrown after an <c>await</c> is handed to the error handler instead of escaping.
    /// </summary>
    [Fact]
    public async Task RunAsync_ActionThrowsAfterAwait_PassesExceptionToErrorHandler()
    {
        var errors = new List<Exception>();
        var failure = new ObjectDisposedException("disk");

        await RelayCommand.RunAsync(async _ =>
        {
            await Task.Yield();
            throw failure;
        }, parameter: null, errors.Add);

        Assert.Same(failure, Assert.Single(errors));
    }

    /// <summary>
    /// An exception thrown before the first <c>await</c>, or by a non-async delegate, is handed over too.
    /// </summary>
    [Fact]
    public async Task RunAsync_ActionThrowsSynchronously_PassesExceptionToErrorHandler()
    {
        var errors = new List<Exception>();

        await RelayCommand.RunAsync(_ => throw new InvalidOperationException("boom"), parameter: null, errors.Add);

        Assert.Equal("boom", Assert.Single(errors).Message);
    }

    /// <summary>
    /// A cancelled operation is not reported as a failure.
    /// </summary>
    [Fact]
    public async Task RunAsync_ActionIsCancelled_DoesNotCallErrorHandler()
    {
        var errors = new List<Exception>();

        await RelayCommand.RunAsync(_ => Task.FromCanceled(new CancellationToken(canceled: true)), parameter: null, errors.Add);

        Assert.Empty(errors);
    }

    /// <summary>
    /// The command parameter reaches the asynchronous action.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithParameter_PassesItToTheAction()
    {
        object? received = null;

        await RelayCommand.RunAsync(p =>
        {
            received = p;
            return Task.CompletedTask;
        }, parameter: "disk", _ => { });

        Assert.Equal("disk", received);
    }

    /// <summary>
    /// A command from <see cref="RelayCommand.FromAsync"/> reports a failure through the handler
    /// when executed like a bound command, and does not throw to the caller.
    /// </summary>
    [Fact]
    public async Task Execute_FromAsyncCommandFails_ReportsToErrorHandlerWithoutThrowing()
    {
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = RelayCommand.FromAsync(
            async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("late");
            },
            onError: ex => reported.TrySetResult(ex));

        command.Execute(null);

        var exception = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("late", exception.Message);
    }

    /// <summary>
    /// The can-execute delegate given to <see cref="RelayCommand.FromAsync"/> is honoured, and a
    /// missing one means always executable.
    /// </summary>
    [Fact]
    public void CanExecute_FromAsyncCommand_UsesTheGivenDelegate()
    {
        var always = RelayCommand.FromAsync(_ => Task.CompletedTask, _ => { });
        var never = RelayCommand.FromAsync(_ => Task.CompletedTask, _ => { }, _ => false);

        Assert.True(always.CanExecute(null));
        Assert.False(never.CanExecute(null));
    }
}
