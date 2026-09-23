using System.IO.Pipes;

namespace ManagedDrive.HelperProtocol;

/// <summary>
/// Client used by the user-mode app to ask the SYSTEM helper service to publish or remove a
/// global DOS-device symlink. Every call is best-effort: if the service is not installed or not
/// running, the call fails silently (<c>false</c>) and the caller degrades gracefully — the disk
/// still mounts normally, just without cross-session visibility.
/// </summary>
public static class HelperPipeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Upper bound on waiting for the service's response line. The service only performs a quick
    /// DOS-device symlink publish/remove, so this is purely a deadlock guard against a wedged or
    /// unresponsive service — without it, a connected-but-silent service would block the caller
    /// forever even though every call is documented as best-effort.
    /// </summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Asks the service to publish a global symlink <paramref name="letter"/> →
    /// <paramref name="devicePath"/>.
    /// </summary>
    public static bool TryPublish(string letter, string devicePath, out HelperResponse response) =>
        TrySend(new(HelperPipeProtocol.OpPublish, letter, devicePath), out response);

    /// <summary>
    /// Asks the service to remove the global symlink previously published for
    /// <paramref name="letter"/>.
    /// </summary>
    public static bool TryUnpublish(string letter, out HelperResponse response) =>
        TrySend(new(HelperPipeProtocol.OpUnpublish, letter, null), out response);

    /// <summary>
    /// Checks whether the helper service is installed and listening.
    /// </summary>
    public static bool IsServiceAvailable() =>
        TrySend(new(HelperPipeProtocol.OpPing, null, null), out var response) && response.Success;

    private static bool TrySend(HelperRequest request, out HelperResponse response)
    {
        response = new(false, string.Empty);

        using var pipe = new NamedPipeClientStream(".", HelperPipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            pipe.Connect(ConnectTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true);
        writer.AutoFlush = true;

        writer.WriteLine(HelperPipeProtocol.SerializeRequest(request));

        using var readCts = new CancellationTokenSource(ReadTimeout);
        string? responseJson;
        try
        {
            responseJson = reader.ReadLineAsync(readCts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (responseJson == null)
        {
            return false;
        }

        response = HelperPipeProtocol.DeserializeResponse(responseJson);
        return true;
    }
}
