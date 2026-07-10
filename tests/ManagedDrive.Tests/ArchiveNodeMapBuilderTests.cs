using System.IO.Compression;
using ManagedDrive.Core;

namespace ManagedDrive.Tests;

public sealed class ArchiveNodeMapBuilderTests
{
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
            Assert.Equal("hello world"u8.ToArray(), file.FileData![..11]);

            Assert.True(nodeMap.TryGet("\\Root.txt", out var rootFile));
            Assert.Equal("root content"u8.ToArray(), rootFile!.FileData![..12]);
        }
        finally
        {
            File.Delete(path);
        }
    }

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
