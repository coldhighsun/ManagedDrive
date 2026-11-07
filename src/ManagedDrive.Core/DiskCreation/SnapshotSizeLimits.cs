namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// The range offered for a disk's snapshot total-size limit. Snapshots live next to the disk's
/// image file, so the limit is bounded by the free space of the image's volume — not by memory,
/// which only bounds the RAM disk's own capacity.
/// </summary>
public static class SnapshotSizeLimits
{
    private const ulong BytesPerMb = 1024UL * 1024;
    private const ulong BytesPerGb = 1024UL * BytesPerMb;

    /// <summary>
    /// The upper bound offered when the image volume's free space cannot be determined (no image
    /// path yet, a network path, an unreadable drive): 1 TB.
    /// </summary>
    public const ulong FallbackMaxBytes = 1024UL * BytesPerGb;

    /// <summary>
    /// The smallest upper bound ever offered (1 MB, the smallest limit that can be set), so a
    /// nearly full volume does not shrink the range to nothing. The slider still has at least one
    /// step in the GB unit (see <see cref="ByteUnitConverter.MaxValueForUnit"/>).
    /// </summary>
    public const ulong MinimumMaxBytes = BytesPerMb;

    /// <summary>
    /// Returns the largest snapshot size limit (in bytes) to offer for a new value.
    /// </summary>
    /// <param name="freeBytesOnImageVolume">
    /// The free space of the volume holding the image file, or <c>null</c> when unknown.
    /// </param>
    /// <returns>
    /// The free space, but at least <see cref="MinimumMaxBytes"/>; or
    /// <see cref="FallbackMaxBytes"/> when the free space is unknown.
    /// </returns>
    public static ulong ResolveMaxBytes(ulong? freeBytesOnImageVolume) =>
        freeBytesOnImageVolume is { } free ? Math.Max(free, MinimumMaxBytes) : FallbackMaxBytes;
}
