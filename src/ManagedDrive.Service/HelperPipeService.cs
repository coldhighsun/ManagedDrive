using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ManagedDrive.HelperProtocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ManagedDrive.Service;

/// <summary>
/// The service's background worker: reconciles stale symlinks at startup, then serves the named
/// pipe (one connection at a time, mirroring the app's CLI pipe server) so the user-mode app can
/// request publish/unpublish operations. A <see cref="PeriodicTimer"/> re-runs reconciliation to
/// reclaim letters leaked by an app that crashed without unpublishing.
/// </summary>
public sealed class HelperPipeService(GlobalMountManager mountManager, ILogger<HelperPipeService> logger)
    : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mountManager.Reconcile();

        _ = Task.Run(() => ReconcileLoopAsync(stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                await HandleConnectionAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Best-effort — a malformed or interrupted request must not take down the loop.
                logger.LogWarning(ex, "Pipe connection handling failed.");
            }
        }
    }

    private async Task ReconcileLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ReconcileInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                mountManager.Reconcile();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        logger.LogDebug("Client connected. PID={Pid}", NativeMethods.GetClientProcessId(pipe.SafePipeHandle));

        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        var requestJson = await reader.ReadLineAsync(ct);
        if (requestJson == null)
        {
            return;
        }

        var request = HelperPipeProtocol.DeserializeRequest(requestJson);
        var response = Handle(request);

        await writer.WriteLineAsync(HelperPipeProtocol.SerializeResponse(response).AsMemory(), ct);
    }

    private HelperResponse Handle(HelperRequest request)
    {
        switch (request.Op)
        {
            case HelperPipeProtocol.OpPing:
                return new HelperResponse(true, "pong");

            case HelperPipeProtocol.OpPublish:
                if (request.Letter == null || request.DevicePath == null)
                {
                    return new HelperResponse(false, "publish requires Letter and DevicePath.");
                }

                var (pubOk, pubMsg) = mountManager.Publish(request.Letter, request.DevicePath);
                return new HelperResponse(pubOk, pubMsg);

            case HelperPipeProtocol.OpUnpublish:
                if (request.Letter == null)
                {
                    return new HelperResponse(false, "unpublish requires Letter.");
                }

                var (unpubOk, unpubMsg) = mountManager.Unpublish(request.Letter);
                return new HelperResponse(unpubOk, unpubMsg);

            default:
                return new HelperResponse(false, $"Unknown op '{request.Op}'.");
        }
    }

    /// <summary>
    /// Creates a pipe whose DACL explicitly allows medium-integrity user processes to connect —
    /// required because this SYSTEM-hosted pipe would otherwise be inaccessible to the user-mode
    /// app across the integrity boundary.
    /// </summary>
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            HelperPipeProtocol.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }
}
