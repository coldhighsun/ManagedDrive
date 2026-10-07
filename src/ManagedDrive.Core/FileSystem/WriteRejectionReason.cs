namespace ManagedDrive.Core.FileSystem;

/// <summary>
/// Why a <see cref="MemoryFileSystem"/> refused a write or a growing resize, reported through
/// <see cref="Mounting.RamDisk.WriteRejected"/> so the user can be told why a copy failed.
/// </summary>
public enum WriteRejectionReason
{
    /// <summary>
    /// The low-memory guard refused the operation because it would leave too little physical
    /// memory available system-wide, although the volume itself still had room.
    /// </summary>
    LowMemory = 0,

    /// <summary>
    /// The operation would have grown the volume past its capacity.
    /// </summary>
    DiskFull = 1,
}
