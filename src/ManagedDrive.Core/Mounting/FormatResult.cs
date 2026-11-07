namespace ManagedDrive.Core.Mounting;

/// <summary>
/// The outcome of <see cref="RamDisk.Format"/>.
/// </summary>
/// <param name="Formatted">
/// <c>true</c> if the disk was emptied; <c>false</c> if it is read-only and was left alone.
/// </param>
/// <param name="CleanedImagePaths">
/// The image files whose image and snapshots were deleted by the format (the disk's current one
/// is written again, empty, right afterwards). Empty when the disk has no image, or was not
/// formatted.
/// </param>
public sealed record FormatResult(bool Formatted, IReadOnlyList<string> CleanedImagePaths);
