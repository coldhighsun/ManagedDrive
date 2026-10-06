using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ManagedDrive.Tests;

public sealed class ReparsePointPersistenceTests : IDisposable
{
    private const uint SymlinkTag = 0xA000000C;
    private const uint MountPointTag = 0xA0000003;
    private const ulong Capacity = 4UL * 1024 * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    public ReparsePointPersistenceTests() => Directory.CreateDirectory(_dir);

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
    [InlineData(ImageCompressionLevel.None, true)]
    [InlineData(ImageCompressionLevel.Fastest, false)]
    [InlineData(ImageCompressionLevel.Fastest, true)]
    public void SaveIncremental_MapWithLinks_RoundTripsLinksAndOrdinaryNodes(ImageCompressionLevel level, bool encrypted)
    {
        var path = ImagePath();
        var map = BuildMapWithLinks(out var symlinkData, out var junctionData);
        ImageEncryptionInfo? encryption = encrypted ? new("s3cret", DiskImageSerializer.GenerateCek()) : null;

        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, level, encryption);
        var loaded = DiskImageSerializer.Load(path, out _, out _, encrypted ? "s3cret" : null, out _);

        Assert.Equal(map.Count, loaded.Count);
        Assert.True(loaded.TryGet("\\file-link", out var symlink));
        Assert.Equal(symlinkData, symlink!.ReparseData);
        Assert.Equal(SymlinkTag, symlink.FileInfo.ReparseTag);
        Assert.NotEqual(0U, symlink.FileInfo.FileAttributes & (uint)FileAttributes.ReparsePoint);
        Assert.True(loaded.TryGet("\\junction", out var junction));
        Assert.Equal(junctionData, junction!.ReparseData);
        Assert.True(junction.IsDirectory);
        Assert.Equal(MountPointTag, junction.FileInfo.ReparseTag);
        Assert.True(loaded.TryGet("\\plain.txt", out var plain));
        Assert.Null(plain!.ReparseData);
        Assert.Equal(0U, plain.FileInfo.ReparseTag);
        Assert.Equal("hello"u8.ToArray(), plain.FileData!.ToArray((long)plain.FileInfo.FileSize));
    }

    [Fact]
    public void SaveIncremental_Always_WritesImageVersion7()
    {
        var path = ImagePath();

        DiskImageSerializer.SaveIncremental(BuildMapWithLinks(out _, out _), Capacity, "Label", path, ImageCompressionLevel.None);

        Assert.Equal(7, ReadVersion(path));
    }

    [Fact]
    public void SaveIncremental_LinkTargetChangedBetweenSaves_LoadsTheNewTarget()
    {
        var path = ImagePath();
        var map = BuildMapWithLinks(out _, out _);
        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, ImageCompressionLevel.None);
        Assert.True(map.TryGet("\\file-link", out var link));
        var newTarget = MakeReparse(SymlinkTag, 40);
        link!.ApplyReparseData(newTarget);
        link.MetadataVersion++;

        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, ImageCompressionLevel.None);
        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);

        Assert.True(loaded.TryGet("\\file-link", out var reloaded));
        Assert.Equal(newTarget, reloaded!.ReparseData);
    }

    [Fact]
    public void SaveIncremental_LinkRemovedBetweenSaves_LoadsAnOrdinaryNode()
    {
        var path = ImagePath();
        var map = BuildMapWithLinks(out _, out _);
        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, ImageCompressionLevel.None);
        Assert.True(map.TryGet("\\file-link", out var link));
        link!.ApplyReparseData(null);
        link.MetadataVersion++;

        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, ImageCompressionLevel.None);
        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);

        Assert.True(loaded.TryGet("\\file-link", out var reloaded));
        Assert.Null(reloaded!.ReparseData);
        Assert.Equal(0U, reloaded.FileInfo.FileAttributes & (uint)FileAttributes.ReparsePoint);
    }

    [Theory]
    [InlineData(ImageCompressionLevel.None, false)]
    [InlineData(ImageCompressionLevel.None, true)]
    [InlineData(ImageCompressionLevel.Fastest, false)]
    [InlineData(ImageCompressionLevel.Fastest, true)]
    public void Save_MapWithLinks_WritesContinuousVersion7AndKeepsTheLinks(ImageCompressionLevel level, bool encrypted)
    {
        var path = ImagePath();
        var map = BuildMapWithLinks(out var symlinkData, out var junctionData);
        ImageEncryptionInfo? encryption = encrypted ? new("s3cret", DiskImageSerializer.GenerateCek()) : null;

        DiskImageSerializer.Save(map, Capacity, "Label", path, level, encryption);
        var loaded = DiskImageSerializer.Load(path, out var capacity, out var label, encrypted ? "s3cret" : null, out _);

        Assert.Equal(7, ReadVersion(path));
        Assert.Equal(0, ReadLayoutByte(path));
        Assert.Equal(Capacity, capacity);
        Assert.Equal("Label", label);
        Assert.Equal(map.Count, loaded.Count);
        Assert.True(loaded.TryGet("\\file-link", out var symlink));
        Assert.Equal(symlinkData, symlink!.ReparseData);
        Assert.True(loaded.TryGet("\\junction", out var junction));
        Assert.Equal(junctionData, junction!.ReparseData);
        Assert.True(loaded.TryGet("\\plain.txt", out var plain));
        Assert.Equal("hello"u8.ToArray(), plain!.FileData!.ToArray((long)plain.FileInfo.FileSize));
    }

    [Fact]
    public void Save_MapWithoutLinks_StillWritesVersion5()
    {
        var path = ImagePath();
        var map = BuildMapWithLinks(out _, out _);
        map.Remove("\\file-link");
        map.Remove("\\junction");

        DiskImageSerializer.Save(map, Capacity, "Label", path, ImageCompressionLevel.None);

        Assert.Equal(5, ReadVersion(path));
        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public void SaveIncremental_NodeTooLargeForSegments_FallsBackToContinuousLayoutAndKeepsLinks()
    {
        var path = ImagePath();
        var map = BuildMapWithLinks(out var symlinkData, out _);

        // An allocation above the segment writer's safe limit diverts the save to Save. The node
        // has no content, so nothing that large is really allocated.
        Assert.True(map.TryGet("\\plain.txt", out var plain));
        plain!.FileInfo.AllocationSize = 2UL * 1024 * 1024 * 1024;

        DiskImageSerializer.SaveIncremental(map, 4UL * 1024 * 1024 * 1024, "Label", path, ImageCompressionLevel.None);

        Assert.Equal(7, ReadVersion(path));
        Assert.Equal(0, ReadLayoutByte(path));
        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);
        Assert.True(loaded.TryGet("\\file-link", out var symlink));
        Assert.Equal(symlinkData, symlink!.ReparseData);
    }

    [Fact]
    public void SaveIncremental_OverContinuousVersion7Image_WritesSegmentedAndKeepsLinks()
    {
        var path = ImagePath();
        var map = BuildMapWithLinks(out var symlinkData, out _);
        DiskImageSerializer.Save(map, Capacity, "Label", path, ImageCompressionLevel.None);

        // The existing image has no segment index to reuse; the save must not trip over that.
        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, ImageCompressionLevel.None);

        Assert.Equal(7, ReadVersion(path));
        Assert.Equal(1, ReadLayoutByte(path));
        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);
        Assert.Equal(map.Count, loaded.Count);
        Assert.True(loaded.TryGet("\\file-link", out var symlink));
        Assert.Equal(symlinkData, symlink!.ReparseData);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PeekHeader_Version7Image_ReadsCapacityLabelAndEncryption(bool segmented)
    {
        var path = ImagePath();
        var map = BuildMapWithLinks(out _, out _);
        if (segmented)
        {
            DiskImageSerializer.SaveIncremental(map, Capacity, "PeekLabel", path, ImageCompressionLevel.None);
        }
        else
        {
            DiskImageSerializer.Save(map, Capacity, "PeekLabel", path, ImageCompressionLevel.None);
        }

        DiskImageSerializer.PeekHeader(path, out var capacity, out var label, out var isEncrypted);

        Assert.Equal(Capacity, capacity);
        Assert.Equal("PeekLabel", label);
        Assert.False(isEncrypted);
    }

    [Fact]
    public void Load_Version7ImageWithUnknownLayoutByte_ThrowsInvalidData()
    {
        var path = ImagePath();
        DiskImageSerializer.SaveIncremental(BuildMapWithLinks(out _, out _), Capacity, "Label", path, ImageCompressionLevel.None);
        var bytes = File.ReadAllBytes(path);
        bytes[LayoutByteOffset] = 9;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => DiskImageSerializer.Load(path, out _, out _, null, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_ImageWhoseRootClaimsToBeALink_LoadsRootAsPlainDirectoryAndKeepsOtherLinks(bool segmented)
    {
        var path = ImagePath();
        var map = BuildMapWithLinks(out var symlinkData, out _);
        Assert.True(map.TryGet("\\", out var root));
        root!.ApplyReparseData(MakeReparse(MountPointTag, 20));
        if (segmented)
        {
            DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, ImageCompressionLevel.None);
        }
        else
        {
            DiskImageSerializer.Save(map, Capacity, "Label", path, ImageCompressionLevel.None);
        }

        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);

        Assert.True(loaded.TryGet("\\", out var loadedRoot));
        Assert.Null(loadedRoot!.ReparseData);
        Assert.Equal(0U, loadedRoot.FileInfo.ReparseTag);
        Assert.Equal(0U, loadedRoot.FileInfo.FileAttributes & (uint)FileAttributes.ReparsePoint);
        Assert.True(loadedRoot.IsDirectory);
        Assert.True(loaded.TryGet("\\file-link", out var symlink));
        Assert.Equal(symlinkData, symlink!.ReparseData);
    }

    [Fact]
    public void SnapshotStore_Load_RootClaimsToBeALink_LoadsRootAsPlainDirectory()
    {
        var map = BuildMapWithLinks(out var symlinkData, out _);
        Assert.True(map.TryGet("\\", out var root));
        root!.ApplyReparseData(MakeReparse(MountPointTag, 20));
        var index = Path.Combine(_dir, "snap.mdrs");
        var blobs = Path.Combine(_dir, "blobs");
        SnapshotStore.Write(map, Capacity, "Label", index, blobs, ImageCompressionLevel.None, cek: null);

        var loaded = SnapshotStore.Load(index, blobs, out _, out _, cek: null);

        Assert.True(loaded.TryGet("\\", out var loadedRoot));
        Assert.Null(loadedRoot!.ReparseData);
        Assert.Equal(0U, loadedRoot.FileInfo.FileAttributes & (uint)FileAttributes.ReparsePoint);
        Assert.True(loaded.TryGet("\\file-link", out var symlink));
        Assert.Equal(symlinkData, symlink!.ReparseData);
    }

    [Fact]
    public void Load_Version6Image_LoadsNodesAndFirstSaveUpgradesItToVersion7()
    {
        var path = ImagePath();
        WriteVersion6Image(path);

        var loaded = DiskImageSerializer.Load(path, out var capacity, out var label, null, out _);

        Assert.Equal(Capacity, capacity);
        Assert.Equal("Old", label);
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.TryGet("\\old.txt", out var oldFile));
        Assert.Null(oldFile!.ReparseData);
        Assert.Equal("legacy"u8.ToArray(), oldFile.FileData!.ToArray((long)oldFile.FileInfo.FileSize));

        // A link added to the loaded disk must survive the save over the version 6 image.
        var link = new FileNode { FileInfo = { FileAttributes = (uint)FileAttributes.Normal } };
        link.ApplyReparseData(MakeReparse(SymlinkTag, 12));
        loaded.Add("\\new-link", link);
        DiskImageSerializer.SaveIncremental(loaded, Capacity, "Old", path, ImageCompressionLevel.None);

        Assert.Equal(7, ReadVersion(path));
        var reloaded = DiskImageSerializer.Load(path, out _, out _, null, out _);
        Assert.Equal(3, reloaded.Count);
        Assert.True(reloaded.TryGet("\\new-link", out var reloadedLink));
        Assert.Equal(link.ReparseData, reloadedLink!.ReparseData);
        Assert.True(reloaded.TryGet("\\old.txt", out var reloadedOld));
        Assert.Equal("legacy"u8.ToArray(), reloadedOld!.FileData!.ToArray((long)reloadedOld.FileInfo.FileSize));
    }

    [Fact]
    public void Load_Version6ImageWithReparseBitButNoBuffer_ClearsTheBit()
    {
        var path = ImagePath();
        WriteVersion6Image(path, staleReparseBit: true);

        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);

        Assert.True(loaded.TryGet("\\old.txt", out var node));
        Assert.Equal(0U, node!.FileInfo.FileAttributes & (uint)FileAttributes.ReparsePoint);
        Assert.Equal(0U, node.FileInfo.ReparseTag);
    }

    [Fact]
    public void NodeMetadataIO_RoundTripWithReparse_PreservesTheBuffer()
    {
        var data = MakeReparse(SymlinkTag, 24);
        var node = new FileNode { FileSecurity = [1, 2, 3] };
        node.ApplyReparseData(data);

        var metadata = RoundTrip("\\link", node, includeReparse: true);

        Assert.Equal(data, metadata.ReparseData);
        Assert.Equal([1, 2, 3], metadata.Security);
    }

    [Fact]
    public void NodeMetadataIO_RoundTripWithReparseLayoutAndOrdinaryNode_ReadsNoBuffer()
    {
        var metadata = RoundTrip("\\file", new FileNode(), includeReparse: true);

        Assert.Null(metadata.ReparseData);
    }

    [Fact]
    public void NodeMetadataIO_LegacyLayout_OmitsTheReparseField()
    {
        var node = new FileNode();
        node.ApplyReparseData(MakeReparse(SymlinkTag, 24));

        var withField = Serialize("\\link", node, includeReparse: true);
        var legacy = Serialize("\\link", node, includeReparse: false);

        Assert.Equal(withField.Length - sizeof(int) - node.ReparseData!.Length, legacy.Length);
        using var reader = new BinaryReader(new MemoryStream(legacy));
        Assert.Null(NodeMetadataIO.ReadMetadata(reader, includeReparse: false).ReparseData);
        Assert.Equal(legacy.Length, reader.BaseStream.Position);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16 * 1024 + 1)]
    [InlineData(int.MaxValue)]
    public void NodeMetadataIO_ReparseLengthOutOfRange_ThrowsInvalidData(int length)
    {
        var bytes = SerializeWithReparseLength("\\link", length, payload: []);

        using var reader = new BinaryReader(new MemoryStream(bytes));
        Assert.Throws<InvalidDataException>(() => NodeMetadataIO.ReadMetadata(reader, includeReparse: true));
    }

    [Fact]
    public void NodeMetadataIO_ReparseBufferWithUnsupportedTag_ThrowsInvalidData()
    {
        var payload = MakeReparse(0xA000001D, 8);
        var bytes = SerializeWithReparseLength("\\link", payload.Length, payload);

        using var reader = new BinaryReader(new MemoryStream(bytes));
        Assert.Throws<InvalidDataException>(() => NodeMetadataIO.ReadMetadata(reader, includeReparse: true));
    }

    [Fact]
    public void NodeMetadataIO_ReparseBufferTruncated_ThrowsEndOfStream()
    {
        var payload = MakeReparse(SymlinkTag, 24);
        var bytes = SerializeWithReparseLength("\\link", payload.Length, payload[..10]);

        using var reader = new BinaryReader(new MemoryStream(bytes));
        Assert.Throws<EndOfStreamException>(() => NodeMetadataIO.ReadMetadata(reader, includeReparse: true));
    }

    [Fact]
    public void SnapshotStore_WriteThenLoad_RoundTripsLinks()
    {
        var map = BuildMapWithLinks(out var symlinkData, out var junctionData);
        var index = Path.Combine(_dir, "snap.mdrs");
        var blobs = Path.Combine(_dir, "blobs");

        SnapshotStore.Write(map, Capacity, "Label", index, blobs, ImageCompressionLevel.None, cek: null);
        var loaded = SnapshotStore.Load(index, blobs, out _, out _, cek: null);

        Assert.Equal(map.Count, loaded.Count);
        Assert.True(loaded.TryGet("\\file-link", out var symlink));
        Assert.Equal(symlinkData, symlink!.ReparseData);
        Assert.Equal(SymlinkTag, symlink.FileInfo.ReparseTag);
        Assert.True(loaded.TryGet("\\junction", out var junction));
        Assert.Equal(junctionData, junction!.ReparseData);
        Assert.True(loaded.TryGet("\\plain.txt", out var plain));
        Assert.Equal("hello"u8.ToArray(), plain!.FileData!.ToArray((long)plain.FileInfo.FileSize));
    }

    [Fact]
    public void SnapshotStore_ReadEntries_ListsLinksAsEntries()
    {
        var map = BuildMapWithLinks(out _, out _);
        var index = Path.Combine(_dir, "snap.mdrs");

        SnapshotStore.Write(map, Capacity, "Label", index, Path.Combine(_dir, "blobs"), ImageCompressionLevel.None, cek: null);
        var entries = SnapshotStore.ReadEntries(index);

        Assert.Contains(entries, entry => entry.Path == "\\file-link" && !entry.IsDirectory);
        Assert.Contains(entries, entry => entry.Path == "\\junction" && entry.IsDirectory);
    }

    [Fact]
    public void SnapshotStore_Load_Version1Index_LoadsNodesWithoutLinks()
    {
        var index = Path.Combine(_dir, "old.mdrs");
        using (var stream = File.Create(index))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("MDRS"u8.ToArray());
            writer.Write(1);
            writer.Write((byte)0);
            writer.Write(Capacity);
            writer.Write("Old");
            writer.Write(2);
            NodeMetadataIO.WriteMetadata(writer, "\\", DirectoryNode());
            NodeMetadataIO.WriteMetadata(writer, "\\empty.txt", new FileNode { FileInfo = { FileAttributes = (uint)FileAttributes.Normal } });
            writer.Write((byte)0); // EmptyFile marker
        }

        var loaded = SnapshotStore.Load(index, Path.Combine(_dir, "blobs"), out var capacity, out var label, cek: null);

        Assert.Equal(Capacity, capacity);
        Assert.Equal("Old", label);
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.TryGet("\\empty.txt", out var file));
        Assert.Null(file!.ReparseData);
    }

    private string ImagePath() => Path.Combine(_dir, $"{Guid.NewGuid()}.mdr");

    private static FileNodeMap BuildMapWithLinks(out byte[] symlinkData, out byte[] junctionData)
    {
        symlinkData = MakeReparse(SymlinkTag, 30);
        junctionData = MakeReparse(MountPointTag, 20);

        var map = new FileNodeMap();
        map.Add("\\", DirectoryNode());

        var plain = new FileNode
        {
            FileInfo = { FileAttributes = (uint)FileAttributes.Normal, AllocationSize = 512, FileSize = 5, IndexNumber = 10 },
            FileData = FileContent.FromSpan("hello"u8, 512),
        };
        map.Add("\\plain.txt", plain);

        var symlink = new FileNode { FileInfo = { FileAttributes = (uint)FileAttributes.Normal, IndexNumber = 11 } };
        symlink.ApplyReparseData(symlinkData);
        map.Add("\\file-link", symlink);

        var junction = new FileNode { FileInfo = { FileAttributes = (uint)FileAttributes.Directory, IndexNumber = 12 } };
        junction.ApplyReparseData(junctionData);
        map.Add("\\junction", junction);

        return map;
    }

    private static FileNode DirectoryNode() => new() { FileInfo = { FileAttributes = (uint)FileAttributes.Directory } };

    private static byte[] MakeReparse(uint tag, int payloadLength)
    {
        var data = new byte[8 + payloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(data, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)payloadLength);
        for (var i = 0; i < payloadLength; i++)
        {
            data[8 + i] = (byte)(i + 1);
        }

        return data;
    }

    /// <summary>
    /// Offset of the layout byte in a version 7 image: magic, version, level, encryption flag.
    /// </summary>
    private const int LayoutByteOffset = 4 + sizeof(int) + 1 + 1;

    private static int ReadLayoutByte(string path)
    {
        using var stream = File.OpenRead(path);
        stream.Position = LayoutByteOffset;
        return stream.ReadByte();
    }

    private static int ReadVersion(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        reader.ReadBytes(4);
        return reader.ReadInt32();
    }

    private static NodeMetadataIO.NodeMetadata RoundTrip(string path, FileNode node, bool includeReparse)
    {
        using var reader = new BinaryReader(new MemoryStream(Serialize(path, node, includeReparse)));
        return NodeMetadataIO.ReadMetadata(reader, includeReparse);
    }

    private static byte[] Serialize(string path, FileNode node, bool includeReparse)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            NodeMetadataIO.WriteMetadata(writer, path, node, includeReparse);
        }

        return stream.ToArray();
    }

    private static byte[] SerializeWithReparseLength(string path, int declaredLength, byte[] payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // Everything up to the reparse field, then a hand-written length and payload.
            NodeMetadataIO.WriteMetadata(writer, path, new FileNode(), includeReparse: false);
            writer.Write(declaredLength);
            writer.Write(payload);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Hand-writes an uncompressed, unencrypted version 6 image (the layout that predates reparse
    /// points) holding a root directory and one small file.
    /// </summary>
    private static void WriteVersion6Image(string path, bool staleReparseBit = false)
    {
        var content = "legacy"u8.ToArray();

        using var payload = new MemoryStream();
        using (var payloadWriter = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            NodeMetadataIO.WriteMetadata(payloadWriter, "\\", DirectoryNode());
            payloadWriter.Write(0L);

            var attributes = (uint)FileAttributes.Normal | (staleReparseBit ? (uint)FileAttributes.ReparsePoint : 0);
            var file = new FileNode
            {
                FileInfo = { FileAttributes = attributes, AllocationSize = 512, FileSize = (ulong)content.Length, IndexNumber = 2 },
            };
            NodeMetadataIO.WriteMetadata(payloadWriter, "\\old.txt", file);
            payloadWriter.Write((long)content.Length);
            payloadWriter.Write(content);
        }

        var payloadBytes = payload.ToArray();

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("MDRD"u8.ToArray());
        writer.Write(6);
        writer.Write((byte)ImageCompressionLevel.None);
        writer.Write((byte)0); // not encrypted
        writer.Write(Capacity);
        writer.Write("Old");
        writer.Write(1); // segment count
        writer.Write(2); // node count
        writer.Write((long)payloadBytes.Length);
        writer.Write(SHA256.HashData(payloadBytes));
        writer.Write(payloadBytes);
    }
}
