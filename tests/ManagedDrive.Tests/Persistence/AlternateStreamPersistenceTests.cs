using System.IO.Compression;

namespace ManagedDrive.Tests;

public sealed class AlternateStreamPersistenceTests : IDisposable
{
    private const ulong Capacity = 4UL * 1024 * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    public AlternateStreamPersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Theory]
    [InlineData(ImageCompressionLevel.None, false)]
    [InlineData(ImageCompressionLevel.Fastest, true)]
    public void SaveIncremental_FileWithStreams_RoundTripsTheStreams(ImageCompressionLevel level, bool encrypted)
    {
        var path = ImagePath();
        var map = BuildMapWithStreams();
        ImageEncryptionInfo? encryption = encrypted ? new("s3cret", DiskImageSerializer.GenerateCek()) : null;

        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, level, encryption);
        var loaded = DiskImageSerializer.Load(path, out _, out _, encrypted ? "s3cret" : null, out _);

        Assert.Equal(map.Count, loaded.Count);
        Assert.True(loaded.TryGet("\\b.txt:Zone.Identifier", out var zone));
        Assert.True(zone!.IsStream);
        Assert.Equal("[ZoneTransfer]"u8.ToArray(), zone.FileData!.ToArray((long)zone.FileInfo.FileSize));
        Assert.True(loaded.TryGet("\\dir:note", out var note));
        Assert.Equal("hi"u8.ToArray(), note!.FileData!.ToArray((long)note.FileInfo.FileSize));
        Assert.Equal(["\\b.txt:Zone.Identifier"], loaded.GetStreams("\\b.txt").Select(s => s.Key));
    }

    [Fact]
    public void Save_FileWithStreams_RoundTripsTheStreams()
    {
        var path = ImagePath();
        var map = BuildMapWithStreams();

        DiskImageSerializer.Save(map, Capacity, "Label", path, ImageCompressionLevel.None);
        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);

        Assert.True(loaded.TryGet("\\b.txt:Zone.Identifier", out _));
        Assert.True(loaded.TryGet("\\dir:note", out _));
    }

    [Fact]
    public void SaveIncremental_StreamContentChangedBetweenSaves_LoadsTheNewContent()
    {
        var path = ImagePath();
        var map = BuildMapWithStreams();
        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, ImageCompressionLevel.None);
        Assert.True(map.TryGet("\\b.txt:Zone.Identifier", out var zone));
        zone!.FileData = FileContent.FromSpan("[ZoneTransferX]"u8, 512);
        zone.FileInfo.FileSize = 15;
        zone.ContentVersion++;

        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, ImageCompressionLevel.None);
        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);

        Assert.True(loaded.TryGet("\\b.txt:Zone.Identifier", out var reloaded));
        Assert.Equal("[ZoneTransferX]"u8.ToArray(), reloaded!.FileData!.ToArray((long)reloaded.FileInfo.FileSize));
    }

    [Fact]
    public void Load_OrphanStreamInTheImage_IsDropped()
    {
        var path = ImagePath();
        var map = BuildMapWithStreams();
        map.Add("\\ghost.txt:s", MakeFile("x"u8));
        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, ImageCompressionLevel.None);

        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);

        Assert.False(loaded.TryGet("\\ghost.txt:s", out _));
        Assert.True(loaded.TryGet("\\b.txt:Zone.Identifier", out _));
    }

    [Fact]
    public void SaveIncremental_AfterDroppingAnOrphanOnLoad_DoesNotWriteItBack()
    {
        var path = ImagePath();
        var map = BuildMapWithStreams();
        map.Add("\\ghost.txt:s", MakeFile("x"u8));
        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, ImageCompressionLevel.None);
        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);

        DiskImageSerializer.SaveIncremental(loaded, Capacity, "Label", path, ImageCompressionLevel.None);
        var reloaded = DiskImageSerializer.Load(path, out _, out _, null, out _);

        Assert.False(reloaded.TryGet("\\ghost.txt:s", out _));
        Assert.Equal(loaded.Count, reloaded.Count);
    }

    [Fact]
    public void Load_ContinuousImageWithOrphanStream_DropsIt()
    {
        var path = ImagePath();
        var map = BuildMapWithStreams();
        map.Add("\\ghost.txt:s", MakeFile("x"u8));
        DiskImageSerializer.Save(map, Capacity, "Label", path, ImageCompressionLevel.None);

        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);

        Assert.False(loaded.TryGet("\\ghost.txt:s", out _));
    }

    [Fact]
    public void Load_NodeWithAColonThatIsNotAStreamName_IsKept()
    {
        var path = ImagePath();
        var map = BuildMapWithStreams();
        map.Add("\\C:", MakeFile("imported"u8));
        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, ImageCompressionLevel.None);

        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);

        Assert.True(loaded.TryGet("\\C:", out var kept));
        Assert.Equal("imported"u8.ToArray(), kept!.FileData!.ToArray((long)kept.FileInfo.FileSize));
    }

    [Fact]
    public void SnapshotStore_RoundTrip_KeepsTheStreams()
    {
        var map = BuildMapWithStreams();
        var index = Path.Combine(_dir, "snap.mdrs");
        var blobs = Path.Combine(_dir, "blobs");
        SnapshotStore.Write(map, Capacity, "Label", index, blobs, ImageCompressionLevel.None, cek: null);

        var loaded = SnapshotStore.Load(index, blobs, out _, out _, cek: null);

        Assert.True(loaded.TryGet("\\b.txt:Zone.Identifier", out var zone));
        Assert.Equal("[ZoneTransfer]"u8.ToArray(), zone!.FileData!.ToArray((long)zone.FileInfo.FileSize));
        Assert.True(loaded.TryGet("\\dir:note", out _));
    }

    [Fact]
    public void SnapshotStore_Load_OrphanStream_IsDropped()
    {
        var map = BuildMapWithStreams();
        map.Add("\\ghost.txt:s", MakeFile("x"u8));
        var index = Path.Combine(_dir, "snap.mdrs");
        var blobs = Path.Combine(_dir, "blobs");
        SnapshotStore.Write(map, Capacity, "Label", index, blobs, ImageCompressionLevel.None, cek: null);

        var loaded = SnapshotStore.Load(index, blobs, out _, out _, cek: null);

        Assert.False(loaded.TryGet("\\ghost.txt:s", out _));
        Assert.True(loaded.TryGet("\\b.txt:Zone.Identifier", out _));
    }

    [Fact]
    public void ArchiveNodeMapWriter_FileWithStreams_LeavesTheStreamsOut()
    {
        var map = BuildMapWithStreams();
        var path = Path.Combine(_dir, "export.zip");

        ArchiveNodeMapWriter.WriteArchive(map, path, ArchiveExportFormat.Zip, ImageCompressionLevel.Fastest);

        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(["b.txt", "dir/", "plain.txt"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ArchiveNodeMapBuilder_EntryNamesWithAColon_AreSkipped()
    {
        var path = Path.Combine(_dir, "colon.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "good.txt", "ok");
            WriteEntry(zip, "bad:name.txt", "x");
            WriteEntry(zip, "dir:stream/inner.txt", "x");
        }

        var map = ArchiveNodeMapBuilder.BuildNodeMap(path);

        Assert.Equal(["\\", "\\good.txt"], map.GetAllNodes().Select(n => n.Key));
    }

    private string ImagePath() => Path.Combine(_dir, $"{Guid.NewGuid()}.mdr");

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static FileNodeMap BuildMapWithStreams()
    {
        var map = new FileNodeMap();
        map.Add("\\", new() { FileInfo = { FileAttributes = (uint)FileAttributes.Directory } });
        map.Add("\\plain.txt", MakeFile("plain"u8));
        map.Add("\\b.txt", MakeFile("main"u8));
        map.Add("\\b.txt:Zone.Identifier", MakeFile("[ZoneTransfer]"u8));
        map.Add("\\dir", new() { FileInfo = { FileAttributes = (uint)FileAttributes.Directory } });
        map.Add("\\dir:note", MakeFile("hi"u8));
        return map;
    }

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
}
