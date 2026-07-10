using System.IO.Pipes;

namespace ManagedDrive.App.Cli;

/// <summary>
/// Hosts a named-pipe server on a background task so a second CLI invocation of
/// ManagedDrive.exe can forward its command to the already-running tray instance instead of
/// starting a duplicate process. Commands are dispatched onto the WPF UI thread so
/// <see cref="MainViewModel"/>'s bound <c>Disks</c> collection can be safely updated. Delegates
/// parsing/execution to <c>ManagedDrive.Cli.Core</c>'s <see cref="CliCommandProcessor"/> via the
/// <see cref="ICliDiskController"/> abstraction.
/// </summary>
public sealed class CliPipeServer(MainViewModel mainViewModel) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ICliDiskController _diskController = new MainViewModelCliDiskController(mainViewModel);
    private Task? _acceptLoop;
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _cts.Cancel();

        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort shutdown; the accept loop may already be tearing down.
        }

        _cts.Dispose();
    }

    /// <summary>
    /// Starts accepting connections on a background task. Safe to call once; the loop keeps
    /// running (one connection at a time) until <see cref="Dispose"/> is called.
    /// </summary>
    public void Start()
    {
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                CliPipeProtocol.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await HandleConnectionAsync(pipe, ct);
            }
            catch
            {
                // Best-effort — a malformed or interrupted request must not take down the
                // accept loop for future CLI invocations.
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true);
        writer.AutoFlush = true;

        var requestJson = await reader.ReadLineAsync(ct);
        if (requestJson == null)
        {
            return;
        }

        var args = CliPipeProtocol.DeserializeRequest(requestJson);

        // Marshal onto the UI thread: CliCommandProcessor calls into MainViewModel (via
        // _diskController), which mutates the WPF-bound Disks collection and must not be
        // touched from this pipe thread.
        var result = await Application.Current.Dispatcher.InvokeAsync(
            () => CliCommandProcessor.ExecuteAsync(args, _diskController)).Task.Unwrap();

        await writer.WriteLineAsync(CliPipeProtocol.SerializeResponse(new(result.Output, result.ExitCode)));
    }
}