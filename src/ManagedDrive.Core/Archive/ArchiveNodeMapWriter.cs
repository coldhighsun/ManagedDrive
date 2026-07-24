using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using SharpCompress.Writers.Zip;

namespace ManagedDrive.Core.Archive;

/// <summary>
/// Archive container format supported by <see cref="ArchiveNodeMapWriter"/>.
/// </summary>
public enum ArchiveExportFormat
{
    /// <summary>
    /// Zip archive, written with the Deflate compression method.
    /// </summary>
    Zip = 0,

    /// <summary>
    /// 7z archive, written with the LZMA compression method.
    /// </summary>
    SevenZip = 1,
}

/// <summary>
/// Writes the contents of a <see cref="FileNodeMap"/> out to a zip or 7z archive file, the
/// inverse of <see cref="ArchiveNodeMapBuilder"/>.
/// </summary>
public static class ArchiveNodeMapWriter
{
    /// <summary>
    /// Writes every node in <paramref name="nodeMap"/> to a new archive file at
    /// <paramref name="archivePath"/>, overwriting it if it already exists.
    /// </summary>
    /// <param name="nodeMap">The node map to export.</param>
    /// <param name="archivePath">Destination archive file path.</param>
    /// <param name="format">The archive container format to write.</param>
    /// <param name="level">Compression level applied to the archive.</param>
    /// <param name="progress">Optional progress reporter, updated with a fraction in [0, 1].</param>
    public static void WriteArchive(
        FileNodeMap nodeMap,
        string archivePath,
        ArchiveExportFormat format,
        ImageCompressionLevel level,
        IProgress<double>? progress = null)
    {
        var nodes = nodeMap.GetAllNodes();
        var totalBytes = 0L;
        foreach (var (_, node) in nodes)
        {
            if (!node.IsDirectory)
            {
                totalBytes += (long)node.FileInfo.FileSize;
            }
        }

        using var stream = File.Create(archivePath);
        using var writer = OpenWriter(stream, format, level);

        var processedBytes = 0L;
        foreach (var (path, node) in nodes)
        {
            var entryKey = path.TrimStart('\\').Replace('\\', '/');
            if (entryKey.Length == 0)
            {
                // The root directory itself is implicit in an archive, same as on the read side.
                continue;
            }

            var modificationTime = DateTimeOffset.FromFileTime((long)node.FileInfo.LastWriteTime).UtcDateTime;

            if (node.IsDirectory)
            {
                writer.WriteDirectory(entryKey, modificationTime);
                continue;
            }

            var size = (int)node.FileInfo.FileSize;
            using var entryStream = new MemoryStream(node.FileData ?? [], 0, size, writable: false);
            writer.Write(entryKey, entryStream, modificationTime);

            if (totalBytes > 0)
            {
                processedBytes += size;
                progress?.Report(Math.Clamp((double)processedBytes / totalBytes, 0.0, 1.0));
            }
        }

        progress?.Report(1.0);
    }

    private static IWriter OpenWriter(Stream stream, ArchiveExportFormat format, ImageCompressionLevel level) => format switch
    {
        ArchiveExportFormat.SevenZip => SevenZipWriter.OpenWriter(stream, new SevenZipWriterOptions
        {
            CompressionType = level == ImageCompressionLevel.None ? CompressionType.None : CompressionType.LZMA,
            CompressionLevel = ToSharpCompressLevel(level),
        }),
        _ => new ZipWriter(stream, new ZipWriterOptions(
            level == ImageCompressionLevel.None ? CompressionType.None : CompressionType.Deflate)
        {
            CompressionLevel = ToSharpCompressLevel(level),
        }),
    };

    private static int ToSharpCompressLevel(ImageCompressionLevel level) => level switch
    {
        ImageCompressionLevel.None => 0,
        ImageCompressionLevel.Fastest => 1,
        ImageCompressionLevel.SmallestSize => 9,
        _ => 6,
    };
}