namespace ManagedDrive.Tests;

/// <summary>
/// Tests that <see cref="DiskImageSerializer.Save"/> reports progress sparingly, ends at 1.0 and
/// still stops promptly when cancelled.
/// </summary>
public sealed class DiskImageProgressTests : IDisposable
{
    /// <summary>
    /// Capacity written into the test images.
    /// </summary>
    private const ulong Capacity = 64UL * 1024 * 1024;

    /// <summary>
    /// Scratch directory for the images of one test.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    /// <summary>
    /// Creates the scratch directory.
    /// </summary>
    public DiskImageProgressTests() => Directory.CreateDirectory(_dir);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Saving thousands of small files reports a bounded number of times and ends at 1.0.
    /// </summary>
    [Fact]
    public void Save_ManySmallFiles_ReportsBoundedNumberOfTimesAndEndsAtOne()
    {
        var map = BuildMap(5_000);
        var reported = new List<double>();

        DiskImageSerializer.Save(map, Capacity, "Label", ImagePath(), 0, progress: new DelegateProgress(reported.Add));

        Assert.InRange(reported.Count, 1, 250);
        Assert.Equal(1.0, reported[^1]);
    }

    /// <summary>
    /// Cancelling after the first report stops the save at the next node, not at the next report.
    /// </summary>
    [Fact]
    public void Save_CancelledAfterFirstReport_ThrowsBeforeWritingAllNodes()
    {
        var map = BuildMap(5_000);
        using var cts = new CancellationTokenSource();
        var reports = 0;
        var progress = CancellableProgress.Wrap(
            new DelegateProgress(_ =>
            {
                reports++;
                cts.Cancel();
            }),
            cts.Token);

        Assert.Throws<OperationCanceledException>(
            () => DiskImageSerializer.Save(map, Capacity, "Label", ImagePath(), 0, progress: progress));
        Assert.Equal(1, reports);
    }

    /// <summary>
    /// Builds a map with the root and <paramref name="fileCount"/> small files.
    /// </summary>
    /// <param name="fileCount">How many files to add.</param>
    /// <returns>The populated map.</returns>
    private static FileNodeMap BuildMap(int fileCount)
    {
        var map = new FileNodeMap();
        map.Add("\\", new() { FileInfo = { FileAttributes = (uint)FileAttributes.Directory } });
        for (var i = 0; i < fileCount; i++)
        {
            map.Add($"\\file{i}.txt", MakeFile("x"u8));
        }

        return map;
    }

    /// <summary>
    /// Creates a file node holding <paramref name="content"/>.
    /// </summary>
    /// <param name="content">The file's bytes.</param>
    /// <returns>The node.</returns>
    private static FileNode MakeFile(ReadOnlySpan<byte> content) => new()
    {
        FileInfo =
        {
            FileAttributes = (uint)FileAttributes.Normal,
            AllocationSize = FileNode.AlignToAllocationUnit((ulong)content.Length),
            FileSize = (ulong)content.Length,
            IndexNumber = FileNode.NewIndexNumber(),
        },
        FileData = FileContent.FromSpan(content, FileNode.AlignToAllocationUnit((ulong)content.Length)),
    };

    /// <summary>
    /// Returns a fresh image path inside the scratch directory.
    /// </summary>
    /// <returns>The image path.</returns>
    private string ImagePath() => Path.Combine(_dir, Guid.NewGuid() + ".mdr");

    /// <summary>
    /// A progress reporter that forwards every value to a delegate.
    /// </summary>
    /// <param name="onReport">Receives each reported value.</param>
    private sealed class DelegateProgress(Action<double> onReport) : IProgress<double>
    {
        /// <inheritdoc />
        public void Report(double value) => onReport(value);
    }
}
