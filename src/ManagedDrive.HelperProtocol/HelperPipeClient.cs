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
    /// Asks the service to publish a global symlink <paramref name="letter"/> →
    /// <paramref name="devicePath"/>.
    /// </summary>
    public static bool TryPublish(string letter, string devicePath, out HelperResponse response) =>
        TrySend(new HelperRequest(HelperPipeProtocol.OpPublish, letter, devicePath), out response);

    /// <summary>
    /// Asks the service to remove the global symlink previously published for
    /// <paramref name="letter"/>.
    /// </summary>
    public static bool TryUnpublish(string letter, out HelperResponse response) =>
        TrySend(new HelperRequest(HelperPipeProtocol.OpUnpublish, letter, null), out response);

    /// <summary>
    /// Checks whether the helper service is installed and listening.
    /// </summary>
    public static bool IsServiceAvailable() =>
        TrySend(new HelperRequest(HelperPipeProtocol.OpPing, null, null), out var response) && response.Success;

    private static bool TrySend(HelperRequest request, out HelperResponse response)
    {
        response = new HelperResponse(false, string.Empty);

        using var pipe = new NamedPipeClientStream(".", HelperPipeProtocol.PipeName, PipeDirection.InOut);

        try
        {
            pipe.Connect(ConnectTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        writer.WriteLine(HelperPipeProtocol.SerializeRequest(request));

        var responseJson = reader.ReadLine();
        if (responseJson == null)
        {
            return false;
        }

        response = HelperPipeProtocol.DeserializeResponse(responseJson);
        return true;
    }
}
