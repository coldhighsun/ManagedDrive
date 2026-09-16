namespace ManagedDrive.Cli.Core;

/// <summary>
/// Archive container format for the <c>export --format</c> CLI option. This is a standalone copy
/// of <c>ManagedDrive.Core.Archive.ArchiveExportFormat</c> — <c>Cli.Core</c> must not reference
/// <c>ManagedDrive.Core</c> (see <see cref="ImageCompressionLevel"/>'s remarks). Keep the explicit
/// values in sync with the Core enum; the App layer casts between the two at the CLI/app boundary.
/// </summary>
public enum ArchiveExportFormat
{
    Zip = 0,
    SevenZip = 1,
}
