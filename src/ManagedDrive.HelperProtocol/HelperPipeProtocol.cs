using System.Text.Json;

namespace ManagedDrive.HelperProtocol;

/// <summary>
/// Wire format shared by the SYSTEM helper service's pipe server and <see cref="HelperPipeClient"/>:
/// a single JSON line carrying a <see cref="HelperRequest"/>, answered with a single JSON line
/// carrying a <see cref="HelperResponse"/>. Mirrors the CLI pipe protocol's line-delimited-JSON
/// convention.
/// </summary>
public static class HelperPipeProtocol
{
    /// <summary>
    /// Named pipe the SYSTEM helper service listens on. Distinct GUID suffix from the CLI pipe so
    /// the two channels never collide.
    /// </summary>
    public const string PipeName = "ManagedDrive-Helper-7B1E9C4A-2D5F-4A63-B8E1-6C0A9F2D3E7B";

    /// <summary>
    /// Publishes a global DOS-device symlink for the given drive letter.
    /// </summary>
    public const string OpPublish = "publish";

    /// <summary>
    /// Removes a previously published global DOS-device symlink for the given drive letter.
    /// </summary>
    public const string OpUnpublish = "unpublish";

    /// <summary>
    /// Liveness check; the service answers with <see cref="HelperResponse.Success"/> true.
    /// </summary>
    public const string OpPing = "ping";

    public static HelperRequest DeserializeRequest(string json) =>
        JsonSerializer.Deserialize<HelperRequest>(json) ?? new HelperRequest(string.Empty, null, null);

    public static HelperResponse DeserializeResponse(string json) =>
        JsonSerializer.Deserialize<HelperResponse>(json) ?? new HelperResponse(false, string.Empty);

    public static string SerializeRequest(HelperRequest request) => JsonSerializer.Serialize(request);

    public static string SerializeResponse(HelperResponse response) => JsonSerializer.Serialize(response);
}

/// <summary>
/// A single request to the helper service.
/// </summary>
/// <param name="Op">
/// One of <see cref="HelperPipeProtocol.OpPublish"/>, <see cref="HelperPipeProtocol.OpUnpublish"/>,
/// or <see cref="HelperPipeProtocol.OpPing"/>.
/// </param>
/// <param name="Letter">
/// The drive letter to publish/unpublish, in <c>"X:"</c> form. <c>null</c> for
/// <see cref="HelperPipeProtocol.OpPing"/>.
/// </param>
/// <param name="DevicePath">
/// The NT device path (e.g. <c>\Device\Volume{GUID}</c>) the symlink targets. Required for
/// <see cref="HelperPipeProtocol.OpPublish"/>; <c>null</c> otherwise.
/// </param>
public sealed record HelperRequest(string Op, string? Letter, string? DevicePath);

/// <summary>
/// The helper service's answer to a <see cref="HelperRequest"/>.
/// </summary>
/// <param name="Success">
/// <c>true</c> if the requested operation completed successfully.
/// </param>
/// <param name="Message">
/// Human-readable detail, primarily for diagnostics/logging.
/// </param>
public sealed record HelperResponse(bool Success, string Message);
