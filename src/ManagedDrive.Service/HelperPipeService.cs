using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ManagedDrive.HelperProtocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThrottledLogging;

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

    /// <summary>
    /// Upper bound on reading the request line and writing the response line of a single
    /// connection. Publish/unpublish requests are quick, so unlike the CLI pipe server's timeout
    /// this also effectively bounds the whole exchange. Guards the single-instance accept loop
    /// against a connected client that never sends anything (or never reads the reply), which
    /// would otherwise wedge every other local caller (the app's publish/unpublish requests) behind
    /// it until the service restarts.
    /// </summary>
    private static readonly TimeSpan PerIoTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Pause before retrying after the pipe couldn't be created or a connection couldn't be
    /// accepted, so a persistent failure doesn't turn the accept loop into a busy spin.
    /// </summary>
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mountManager.Reconcile();

        _ = Task.Run(() => ReconcileLoopAsync(stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                connected = true;
                await HandleConnectionAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Best-effort — a malformed or interrupted request must not take down the loop.
                logger.LogWarningThrottled(
                    "pipe-connection-failed", TimeSpan.FromMinutes(5),
                    "Pipe connection handling failed: {Error}", ex.Message);

                if (!connected)
                {
                    // Failing before any client connected (e.g. another process holds the pipe
                    // name) will most likely fail again right away; without a pause this loop
                    // would spin a core at 100% for as long as the condition lasts.
                    try
                    {
                        await Task.Delay(AcceptRetryDelay, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
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

        security.AddAccessRule(new(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        security.AddAccessRule(new(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new(
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

    /// <summary>
    /// Executes <paramref name="request"/> on behalf of the connected user.
    /// </summary>
    /// <param name="request">The decoded request.</param>
    /// <param name="callerSid">
    /// SID of the connected user, or <c>null</c> if it couldn't be determined — publish and
    /// unpublish are refused then, since ownership decides who may change which letter.
    /// </param>
    /// <returns>The response to send back.</returns>
    private HelperResponse Handle(HelperRequest request, string? callerSid)
    {
        if (request.Op is HelperPipeProtocol.OpPublish or HelperPipeProtocol.OpUnpublish && callerSid is null)
        {
            return new(false, "Could not identify the calling user.");
        }

        switch (request.Op)
        {
            case HelperPipeProtocol.OpPing:
                return new(true, "pong");

            case HelperPipeProtocol.OpPublish:
                if (request.Letter == null || request.DevicePath == null)
                {
                    return new(false, "publish requires Letter and DevicePath.");
                }

                var (pubOk, pubMsg) = mountManager.Publish(request.Letter, request.DevicePath, callerSid!);
                return new(pubOk, pubMsg);

            case HelperPipeProtocol.OpUnpublish:
                if (request.Letter == null)
                {
                    return new(false, "unpublish requires Letter.");
                }

                var (unpubOk, unpubMsg) = mountManager.Unpublish(request.Letter, callerSid!);
                return new(unpubOk, unpubMsg);

            default:
                return new(false, $"Unknown op '{request.Op}'.");
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        logger.LogDebug("Client connected. PID={Pid}", NativeMethods.GetClientProcessId(pipe.SafePipeHandle));

        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true);
        writer.AutoFlush = true;

        // Either the connected client never sent a request within PerIoTimeout, or the service
        // itself is stopping — either way, drop this connection without blocking the next one.
        var requestJson = await PipeIo.ReadLineWithTimeoutAsync(reader, PerIoTimeout, ct);
        if (requestJson == null)
        {
            return;
        }

        var request = HelperPipeProtocol.DeserializeRequest(requestJson);
        var response = Handle(request, GetCallerSid(pipe));

        await PipeIo.WriteLineWithTimeoutAsync(writer, HelperPipeProtocol.SerializeResponse(response), PerIoTimeout, ct);
    }

    /// <summary>
    /// Identifies the user on the other end of <paramref name="pipe"/>, after the request has been read.
    /// </summary>
    /// <param name="pipe">The connected pipe.</param>
    /// <returns>The caller's SID, or <c>null</c> if impersonation failed.</returns>
    private string? GetCallerSid(NamedPipeServerStream pipe)
    {
        try
        {
            return NativeMethods.GetClientUserSid(pipe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarningThrottled(
                "pipe-impersonation-failed", TimeSpan.FromMinutes(5),
                "Could not identify the pipe client: {Error}", ex.Message);
            return null;
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
}
