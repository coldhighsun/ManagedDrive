using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security;
using ManagedDrive.HelperProtocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ThrottledLogging;

namespace ManagedDrive.Service;

/// <summary>
/// The service's background worker: reconciles stale symlinks at startup, then serves the named
/// pipe (several connections at once, like the app's CLI pipe server) so the user-mode app can
/// request publish/unpublish operations. A <see cref="PeriodicTimer"/> re-runs reconciliation to
/// reclaim letters leaked by an app that crashed without unpublishing.
/// </summary>
public sealed class HelperPipeService(GlobalMountManager mountManager, ILogger<HelperPipeService> logger)
    : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Most pipe instances alive at once: one listening for the next client, the others serving
    /// connected clients. Any local user can connect, so a single client that connects and then
    /// stalls must not be able to keep the app's own requests from getting through.
    /// </summary>
    internal const int MaxInstances = 8;

    /// <summary>
    /// Upper bound on reading the request line and writing the response line of a single
    /// connection. Publish/unpublish requests are quick and clients send theirs right after
    /// connecting, so this also effectively bounds the whole exchange — and how long a connected
    /// client that never sends anything (or never reads the reply) can hold one of the
    /// <see cref="MaxInstances"/> pipe instances.
    /// </summary>
    private static readonly TimeSpan PerIoTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Pause before retrying after the pipe couldn't be created, so a persistent failure doesn't
    /// turn the accept loop into a busy spin.
    /// </summary>
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The <c>HKLM</c> key holding the service's settings.
    /// </summary>
    internal const string SettingsKeyPath = @"SOFTWARE\ManagedDrive\Helper";

    /// <summary>
    /// DWORD value under <see cref="SettingsKeyPath"/> that, when nonzero, lets every user (not
    /// just administrators) publish and unpublish global drive letters.
    /// </summary>
    internal const string AllowNonAdminPublishValueName = "AllowNonAdminPublish";

    /// <summary>
    /// Failure message sent to a caller refused by <see cref="GlobalMountPolicy.MayChangeGlobalMounts"/>.
    /// </summary>
    internal const string AdministratorsOnlyMessage =
        $@"Only administrators may publish global drive letters. To allow every user, set the DWORD HKLM\{SettingsKeyPath}\{AllowNonAdminPublishValueName} to 1.";

    /// <summary>
    /// Reconciles stale symlinks, then serves the pipe until the service stops.
    /// </summary>
    /// <param name="stoppingToken">Signals that the service is stopping.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mountManager.Reconcile();

        _ = Task.Run(() => ReconcileLoopAsync(stoppingToken), stoppingToken);

        var listener = new PipeListener(
            MaxInstances,
            CreatePipe,
            HandleConnectionAsync,
            ex => logger.LogWarningThrottled(
                "pipe-create-failed", TimeSpan.FromMinutes(5),
                "Failed to create the pipe (another process may be holding its name); retrying: {Error}", ex.Message),
            ex => logger.LogWarningThrottled(
                "pipe-connection-failed", TimeSpan.FromMinutes(5),
                "Pipe connection handling failed: {Error}", ex.Message),
            AcceptRetryDelay);
        await listener.RunAsync(stoppingToken);
    }

    /// <summary>
    /// Creates a listening instance of the service's pipe, secured by
    /// <see cref="CreatePipeSecurity"/>.
    /// </summary>
    /// <param name="firstInstance">
    /// Whether no other instance of the service's pipe exists. The pipe is then created as the
    /// name's first instance, failing if another process already holds the name: joining that
    /// process's pipe would let it take over some of the service's clients.
    /// </param>
    /// <returns>The pipe, waiting for a connection.</returns>
    private static NamedPipeServerStream CreatePipe(bool firstInstance) =>
        NamedPipeServerStreamAcl.Create(
            HelperPipeProtocol.PipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            firstInstance ? PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance : PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            CreatePipeSecurity());

    /// <summary>
    /// Creates a security descriptor whose DACL explicitly allows medium-integrity user processes
    /// to connect — required because this SYSTEM-hosted pipe would otherwise be inaccessible to
    /// the user-mode app across the integrity boundary — but denies remote clients. The pipe is
    /// explicitly owned by <c>LocalSystem</c>, which is how clients tell it from one another user
    /// created under the same name.
    /// </summary>
    /// <returns>The security descriptor to create the pipe with.</returns>
    internal static PipeSecurity CreatePipeSecurity()
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

        PipeSecurityRules.DenyRemoteClients(security);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        return security;
    }

    /// <summary>
    /// Executes <paramref name="request"/> on behalf of the connected user.
    /// </summary>
    /// <param name="request">The decoded request.</param>
    /// <param name="caller">
    /// The connected user, or <c>null</c> if it couldn't be determined — publish and unpublish
    /// are refused then, since group membership and ownership decide who may change which letter.
    /// </param>
    /// <returns>The response to send back.</returns>
    private HelperResponse Handle(HelperRequest request, PipeClientIdentity? caller)
    {
        if (request.Op is HelperPipeProtocol.OpPublish or HelperPipeProtocol.OpUnpublish)
        {
            if (caller is null)
            {
                return new(false, "Could not identify the calling user.");
            }

            if (!GlobalMountPolicy.MayChangeGlobalMounts(caller.GroupSids, AllowsNonAdminChanges()))
            {
                logger.LogWarning("Refused {Op} from non-administrator {Sid}", request.Op, caller.UserSid);
                return new(false, AdministratorsOnlyMessage);
            }
        }

        var callerSid = caller?.UserSid;

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

    /// <summary>
    /// Reads one request from <paramref name="pipe"/>, executes it, and writes the response back.
    /// </summary>
    /// <param name="pipe">The connected pipe instance.</param>
    /// <param name="ct">Stops handling the request.</param>
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

        // Impersonating the caller (via GetCaller) is only meaningful for publish/unpublish,
        // which need it for the authorization checks in Handle(). Skip it for ping — the far more
        // frequent op (e.g. the Settings dialog's helper-service status check) — so a liveness
        // check never pays for an impersonate/revert round trip it doesn't use.
        var caller = request.Op is HelperPipeProtocol.OpPublish or HelperPipeProtocol.OpUnpublish
            ? GetCaller(pipe)
            : null;
        var response = Handle(request, caller);

        await PipeIo.WriteLineWithTimeoutAsync(writer, HelperPipeProtocol.SerializeResponse(response), PerIoTimeout, ct);
    }

    /// <summary>
    /// Identifies the user on the other end of <paramref name="pipe"/>, after the request has been read.
    /// </summary>
    /// <param name="pipe">The connected pipe.</param>
    /// <returns>The caller, or <c>null</c> if impersonation failed.</returns>
    private PipeClientIdentity? GetCaller(NamedPipeServerStream pipe)
    {
        try
        {
            return NativeMethods.GetClientIdentity(pipe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarningThrottled(
                "pipe-impersonation-failed", TimeSpan.FromMinutes(5),
                "Could not identify the pipe client: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reads whether an administrator has let every user change global drive letters. Read on
    /// each request, so toggling the setting takes effect without restarting the service. Fails
    /// closed: a setting that can't be read counts as not set.
    /// </summary>
    /// <returns>
    /// <c>true</c> if <see cref="AllowNonAdminPublishValueName"/> is set to a nonzero DWORD.
    /// </returns>
    private bool AllowsNonAdminChanges()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SettingsKeyPath, writable: false);
            return IsEnabledSetting(key?.GetValue(AllowNonAdminPublishValueName));
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarningThrottled(
                "settings-read-failed", TimeSpan.FromMinutes(5),
                "Could not read the helper settings; allowing administrators only: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Decodes an on/off registry setting.
    /// </summary>
    /// <param name="value">The registry value, or <c>null</c> if it isn't set.</param>
    /// <returns><c>true</c> only for a nonzero DWORD.</returns>
    internal static bool IsEnabledSetting(object? value) => value is int dword && dword != 0;

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
