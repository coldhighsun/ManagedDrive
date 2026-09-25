using System.IO.Compression;

namespace ManagedDrive.Tests;

public sealed class ArchiveNodeMapBuilderTests
{
    [Fact]
    public void BuildNodeMap_ComputesTotalAllocatedFromExtractedContent()
    {
        var path = CreateZip(entries =>
        {
            entries.Add("A.txt", new byte[100]);
            entries.Add("B.txt", new byte[900]);
        });

        try
        {
            var nodeMap = ArchiveNodeMapBuilder.BuildNodeMap(path);

            var expected = FileNode.AlignToAllocationUnit(100) + FileNode.AlignToAllocationUnit(900);
            Assert.Equal(expected, nodeMap.GetTotalAllocated());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildNodeMap_InvalidFile_ThrowsInvalidDataException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
        File.WriteAllBytes(path, "not a zip file"u8.ToArray());

        try
        {
            Assert.Throws<InvalidDataException>(() => ArchiveNodeMapBuilder.BuildNodeMap(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildNodeMap_MultiLevelZip_RestoresDirectoriesAndFileContent()
    {
        var path = CreateZip(entries =>
        {
            entries.Add("Folder/SubFolder/File.txt", "hello world"u8.ToArray());
            entries.Add("Root.txt", "root content"u8.ToArray());
        });

        try
        {
            var nodeMap = ArchiveNodeMapBuilder.BuildNodeMap(path);

            Assert.True(nodeMap.TryGet("\\", out var root));
            Assert.True(root!.IsDirectory);

            Assert.True(nodeMap.TryGet("\\Folder", out var folder));
            Assert.True(folder!.IsDirectory);

            Assert.True(nodeMap.TryGet("\\Folder\\SubFolder", out var subFolder));
            Assert.True(subFolder!.IsDirectory);

            Assert.True(nodeMap.TryGet("\\Folder\\SubFolder\\File.txt", out var file));
            Assert.False(file!.IsDirectory);
            Assert.Equal(11UL, file.FileInfo.FileSize);
            Assert.Equal("hello world"u8.ToArray(), file.FileData!.ToArray(11));

            Assert.True(nodeMap.TryGet("\\Root.txt", out var rootFile));
            Assert.Equal("root content"u8.ToArray(), rootFile!.FileData!.ToArray(12));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildNodeMap_WithoutTotalBytes_ReportsOnlyFinalOne()
    {
        var path = CreateZip(entries =>
        {
            entries.Add("A.txt", new byte[100]);
            entries.Add("B.txt", new byte[900]);
        });

        try
        {
            var reports = new List<double>();
            ArchiveNodeMapBuilder.BuildNodeMap(path, totalBytes: null, progress: new RecordingProgress(reports));

            Assert.Equal([1.0], reports);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildNodeMap_WithTotalBytes_ReportsMonotonicProgressEndingAtOne()
    {
        var path = CreateZip(entries =>
        {
            entries.Add("A.txt", new byte[100]);
            entries.Add("B.txt", new byte[900]);
        });

        try
        {
            ArchiveNodeMapBuilder.PeekArchive(path, out var totalBytes, out _);

            var reports = new List<double>();
            ArchiveNodeMapBuilder.BuildNodeMap(path, (long)totalBytes, new RecordingProgress(reports));

            Assert.NotEmpty(reports);
            Assert.Equal(1.0, reports[^1]);
            for (var i = 1; i < reports.Count; i++)
            {
                Assert.True(reports[i] >= reports[i - 1]);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildNodeMap_CancelledMidExtraction_PropagatesCancellationUnwrapped()
    {
        // Cancellation surfaces through the progress reporter; it must not be mistaken for (and
        // reported to the user as) an unreadable archive.
        var path = CreateZip(entries => entries.Add("A.txt", new byte[100]));

        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                ArchiveNodeMapBuilder.BuildNodeMap(path, 100, new CancellingProgress()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class CancellingProgress : IProgress<double>
    {
        public void Report(double value) => throw new OperationCanceledException();
    }

    [Fact]
    public void PeekArchive_InvalidFile_ThrowsInvalidDataException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
        File.WriteAllBytes(path, "not a zip file"u8.ToArray());

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                ArchiveNodeMapBuilder.PeekArchive(path, out _, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PeekArchive_ValidZip_ReturnsTotalSizeAndSuggestedLabel()
    {
        var path = CreateZip(entries =>
        {
            entries.Add("A.txt", new byte[10]);
            entries.Add("B.txt", new byte[20]);
        });

        try
        {
            ArchiveNodeMapBuilder.PeekArchive(path, out var totalBytes, out var suggestedLabel);

            Assert.Equal(30UL, totalBytes);
            Assert.Equal(Path.GetFileNameWithoutExtension(path), suggestedLabel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("a.txt", "\\a.txt")]
    [InlineData("Folder/File.txt", "\\Folder\\File.txt")]
    [InlineData("Folder/", "\\Folder")]
    [InlineData("./a.txt", "\\a.txt")]
    [InlineData("Folder//File.txt", "\\Folder\\File.txt")]
    [InlineData("Folder/./File.txt", "\\Folder\\File.txt")]
    [InlineData("Folder\\File.txt", "\\Folder\\File.txt")]
    [InlineData("Folder/Sub/../File.txt", "\\Folder\\File.txt")]
    [InlineData("../../evil.txt", "\\evil.txt")]
    [InlineData("/abs/path.txt", "\\abs\\path.txt")]
    public void NormalizeEntryPath_VariousKeys_ReturnsCanonicalRootedPath(string key, string expected)
    {
        var actual = ArchiveNodeMapBuilder.NormalizeEntryPath(key);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("..")]
    [InlineData("a/..")]
    [InlineData("a/../../")]
    public void NormalizeEntryPath_KeyResolvingToRoot_ReturnsNull(string? key)
    {
        var actual = ArchiveNodeMapBuilder.NormalizeEntryPath(key);

        Assert.Null(actual);
    }

    [Fact]
    public void ToFileTimeOrFallback_DateBefore1601_ReturnsFallback()
    {
        var lastModified = new DateTime(1500, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var actual = ArchiveNodeMapBuilder.ToFileTimeOrFallback(lastModified, 42);

        Assert.Equal(42UL, actual);
    }

    [Fact]
    public void ToFileTimeOrFallback_NoDate_ReturnsFallback()
    {
        var actual = ArchiveNodeMapBuilder.ToFileTimeOrFallback(null, 42);

        Assert.Equal(42UL, actual);
    }

    [Fact]
    public void ToFileTimeOrFallback_RepresentableDate_ReturnsFileTime()
    {
        var lastModified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var actual = ArchiveNodeMapBuilder.ToFileTimeOrFallback(lastModified, 42);

        Assert.Equal((ulong)lastModified.ToFileTimeUtc(), actual);
    }

    [Fact]
    public void BuildNodeMap_EntriesWithDotSegments_KeepsRootDirectoryAndCanonicalPaths()
    {
        var path = CreateZip(entries =>
        {
            entries.Add("./a.txt", "a"u8.ToArray());
            entries.Add("x/..", "not a root"u8.ToArray());
            entries.Add("Folder/../../b.txt", "b"u8.ToArray());
        });

        try
        {
            var nodeMap = ArchiveNodeMapBuilder.BuildNodeMap(path);

            Assert.True(nodeMap.TryGet("\\", out var root));
            Assert.True(root!.IsDirectory);
            Assert.True(nodeMap.TryGet("\\a.txt", out var a));
            Assert.Equal("a"u8.ToArray(), a!.FileData!.ToArray(1));
            Assert.True(nodeMap.TryGet("\\b.txt", out var b));
            Assert.Equal("b"u8.ToArray(), b!.FileData!.ToArray(1));
            Assert.False(nodeMap.TryGet("\\.", out _));
            Assert.False(nodeMap.TryGet("\\..", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateZip(Action<Dictionary<string, byte[]>> configure)
    {
        var entries = new Dictionary<string, byte[]>();
        configure(entries);

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var entryStream = entry.Open();
            entryStream.Write(content);
        }

        return path;
    }
}
