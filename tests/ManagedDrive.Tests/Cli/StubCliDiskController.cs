using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

/// <summary>
/// Base for test doubles of <see cref="ICliDiskController"/>: every operation reports "no disk
/// mounted" until a test overrides it, so a double only spells out what its test cares about and
/// a new interface member needs adding here once instead of in every test file.
/// </summary>
internal abstract class StubCliDiskController : ICliDiskController
{
    /// <summary>
    /// The result of an operation on a mount point that is not mounted.
    /// </summary>
    protected static readonly (bool, string) NotMounted = (false, string.Empty);

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> FormatAsync(string mountPoint) => Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> ExportAsync(
        string mountPoint, string outputPath, ManagedDrive.Cli.Core.ArchiveExportFormat? archiveFormat,
        ManagedDrive.Cli.Core.ImageCompressionLevel compressionLevel, string? password) =>
        Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual IReadOnlyList<CliDiskInfo> ListDisks() => [];

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> MountArchiveAsync(string archivePath, string? mountPoint, CliMountOverrides overrides) =>
        Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> MountImageAsync(string imagePath, string mountPoint, CliMountOverrides overrides) =>
        Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> CreateAsync(
        string mountPoint, ulong capacityBytes, string? volumeLabel, string? imagePath, string? password) =>
        Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message, IReadOnlyList<CliFileEntry>? Entries)> ListFilesAsync(string mountPoint, string? path) =>
        Task.FromResult<(bool, string, IReadOnlyList<CliFileEntry>?)>((false, string.Empty, null));

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> CloneAsync(string sourceMountPoint, string targetMountPoint) =>
        Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> CreateSnapshotAsync(string mountPoint) => Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> DeleteSnapshotAsync(string mountPoint, int index) => Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message, IReadOnlyList<CliSnapshotInfo>? Snapshots)> ListSnapshotsAsync(string mountPoint) =>
        Task.FromResult<(bool, string, IReadOnlyList<CliSnapshotInfo>?)>((false, string.Empty, null));

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> RestoreSnapshotAsync(string mountPoint, int index) => Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message, CliSnapshotDiff? Diff)> DiffSnapshotAsync(string mountPoint, int index) =>
        Task.FromResult<(bool, string, CliSnapshotDiff?)>((false, string.Empty, null));

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> EditAsync(
        string mountPoint, ulong? capacityBytes, string? volumeLabel, uint? autoSaveIntervalMinutes, bool disableAutoSave) =>
        Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message, CliDiskDetails? Details)> GetDiskInfoAsync(string mountPoint) =>
        Task.FromResult<(bool, string, CliDiskDetails?)>((false, string.Empty, null));

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> ExtractSnapshotAsync(
        string mountPoint, int index, string snapshotPath, string outputPath, bool overwrite) =>
        Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual Task RequestExitAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> SaveAsync(string mountPoint) => Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual Task<(bool Success, string Message)> SetPasswordAsync(string mountPoint, string? newPassword) => Task.FromResult(NotMounted);

    /// <inheritdoc />
    public virtual Task<(bool Unmounted, string? SaveError)> UnmountAsync(string mountPoint, bool deleteImage) =>
        Task.FromResult<(bool, string?)>((false, null));
}
