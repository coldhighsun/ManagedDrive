using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ManagedDrive.Tests;

public sealed class SnapshotStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    public SnapshotStoreTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void WriteBlob_ContentChangesAfterHashing_FilesBlobUnderHashOfWrittenBytes()
    {
        var blobDirectory = Path.Combine(_dir, "blobs");
        byte[] original = [1, 2, 3, 4];
        byte[] changed = [9, 9, 9, 9];
        var content = FileContent.FromSpan(original, 512);

        var hash = SnapshotStore.WriteBlob(
            blobDirectory, content, original.Length, ImageCompressionLevel.None, cek: null, customZstdLevel: null,
            beforeBlobWrite: () => WriteBytes(content, 0, changed));

        Assert.Equal(SHA256.HashData(changed), hash);
        Assert.False(File.Exists(SnapshotStore.HashToBlobPath(blobDirectory, SHA256.HashData(original))));
        var blob = File.ReadAllBytes(SnapshotStore.HashToBlobPath(blobDirectory, hash));
        Assert.Equal(changed, blob[1..]); // after the flag byte
    }

    [Fact]
    public void WriteBlob_ContentUnchanged_FilesBlobUnderHashOfContent()
    {
        var blobDirectory = Path.Combine(_dir, "blobs");
        byte[] data = [1, 2, 3, 4];
        var content = FileContent.FromSpan(data, 512);

        var hash = SnapshotStore.WriteBlob(blobDirectory, content, data.Length, ImageCompressionLevel.Optimal, cek: null, customZstdLevel: null);

        Assert.Equal(SHA256.HashData(data), hash);
        Assert.True(File.Exists(SnapshotStore.HashToBlobPath(blobDirectory, hash)));
        Assert.Single(Directory.EnumerateFiles(blobDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void WriteBlob_ExistingPlaintextBlobWithCek_ReplacesItWithEncryptedBlob()
    {
        var blobDirectory = Path.Combine(_dir, "blobs");
        byte[] data = [1, 2, 3, 4];
        var content = FileContent.FromSpan(data, 512);
        SnapshotStore.WriteBlob(blobDirectory, content, data.Length, ImageCompressionLevel.None, cek: null, customZstdLevel: null);

        var hash = SnapshotStore.WriteBlob(blobDirectory, content, data.Length, ImageCompressionLevel.None, DiskImageSerializer.GenerateCek(), customZstdLevel: null);

        var blob = File.ReadAllBytes(SnapshotStore.HashToBlobPath(blobDirectory, hash));
        Assert.NotEqual(0, blob[0] & 0b010);
        Assert.Single(Directory.EnumerateFiles(blobDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void WriteBlob_ExistingEncryptedBlobWithoutCek_ReplacesItWithPlaintextBlob()
    {
        var blobDirectory = Path.Combine(_dir, "blobs");
        byte[] data = [1, 2, 3, 4];
        var content = FileContent.FromSpan(data, 512);
        SnapshotStore.WriteBlob(blobDirectory, content, data.Length, ImageCompressionLevel.None, DiskImageSerializer.GenerateCek(), customZstdLevel: null);

        var hash = SnapshotStore.WriteBlob(blobDirectory, content, data.Length, ImageCompressionLevel.None, cek: null, customZstdLevel: null);

        var blob = File.ReadAllBytes(SnapshotStore.HashToBlobPath(blobDirectory, hash));
        Assert.Equal(0, blob[0] & 0b010);
        Assert.Equal(data, blob[1..]);
    }

    [Fact]
    public void WriteBlob_ExistingBlobWithMatchingEncryption_ReusesIt()
    {
        var blobDirectory = Path.Combine(_dir, "blobs");
        byte[] data = [1, 2, 3, 4];
        var content = FileContent.FromSpan(data, 512);
        var cek = DiskImageSerializer.GenerateCek();
        var hash = SnapshotStore.WriteBlob(blobDirectory, content, data.Length, ImageCompressionLevel.None, cek, customZstdLevel: null);
        var blobPath = SnapshotStore.HashToBlobPath(blobDirectory, hash);
        var original = File.ReadAllBytes(blobPath);

        SnapshotStore.WriteBlob(blobDirectory, content, data.Length, ImageCompressionLevel.None, cek, customZstdLevel: null);

        // A rewrite would pick a fresh random nonce, so identical bytes mean the blob was reused.
        Assert.Equal(original, File.ReadAllBytes(blobPath));
    }

    [Fact]
    public void WriteBlob_ExistingBlobLockedExclusively_ReusesItInsteadOfThrowing()
    {
        var blobDirectory = Path.Combine(_dir, "blobs");
        byte[] data = [1, 2, 3, 4];
        var content = FileContent.FromSpan(data, 512);
        var hash = SnapshotStore.WriteBlob(blobDirectory, content, data.Length, ImageCompressionLevel.None, cek: null, customZstdLevel: null);
        var blobPath = SnapshotStore.HashToBlobPath(blobDirectory, hash);

        byte[] result;
        using (new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = SnapshotStore.WriteBlob(blobDirectory, content, data.Length, ImageCompressionLevel.None, cek: null, customZstdLevel: null);
        }

        Assert.Equal(hash, result);
        Assert.Single(Directory.EnumerateFiles(blobDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Write_EncryptedSnapshotAfterPlaintextSnapshotOfSameContent_LoadsWithCek()
    {
        var data = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        var map = new FileNodeMap();
        map.Add("\\", new() { FileInfo = { FileAttributes = (uint)FileAttributes.Directory } });
        map.Add("\\a.bin", new()
        {
            FileData = FileContent.FromSpan(data, 512),
            FileInfo = { FileAttributes = (uint)FileAttributes.Normal, FileSize = 512, AllocationSize = 512 },
        });
        var blobDirectory = Path.Combine(_dir, "blobs");
        var cek = DiskImageSerializer.GenerateCek();
        SnapshotStore.Write(map, 1 << 20, "label", Path.Combine(_dir, "plain.mdr"), blobDirectory, ImageCompressionLevel.Optimal, cek: null);
        var encryptedIndexPath = Path.Combine(_dir, "encrypted.mdr");

        SnapshotStore.Write(map, 1 << 20, "label", encryptedIndexPath, blobDirectory, ImageCompressionLevel.Optimal, cek);
        var loaded = SnapshotStore.Load(encryptedIndexPath, blobDirectory, out _, out _, cek);

        Assert.True(loaded.TryGet("\\a.bin", out var node));
        Assert.Equal(data, node!.FileData!.ToArray(512));
    }

    [Fact]
    public void Write_FileSizeAheadOfContentLength_RecordsSizeMatchingStoredBytes()
    {
        // Mirrors a node caught mid-resize: FileSize already past the content's current length.
        var map = new FileNodeMap();
        map.Add("\\", new() { FileInfo = { FileAttributes = (uint)FileAttributes.Directory } });
        var data = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        map.Add("\\a.bin", new()
        {
            FileData = FileContent.FromSpan(data, 512),
            FileInfo = { FileAttributes = (uint)FileAttributes.Normal, FileSize = 4096, AllocationSize = 4096 },
        });
        var indexPath = Path.Combine(_dir, "snap.mdr");
        var blobDirectory = Path.Combine(_dir, "blobs");

        SnapshotStore.Write(map, 1 << 20, "label", indexPath, blobDirectory, ImageCompressionLevel.Optimal, cek: null);
        var loaded = SnapshotStore.Load(indexPath, blobDirectory, out _, out _, cek: null);

        Assert.True(loaded.TryGet("\\a.bin", out var node));
        Assert.Equal(512UL, node!.FileInfo.FileSize);
        Assert.Equal(data, node.FileData!.ToArray(512));
    }

    private static void WriteBytes(FileContent content, long offset, byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
            content.WriteFrom(ptr, (ulong)offset, (uint)data.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Theory]
    [InlineData(@"C:\disks\disk.mdr", @"C:\disks\disk.snapblobs")]
    [InlineData(@"C:\disks\my.image.mdr", @"C:\disks\my.image.snapblobs")]
    [InlineData(@"C:\disks\noext", @"C:\disks\noext.snapblobs")]
    public void ComputeBlobDirectory_AppendsSnapblobsSuffixNextToImage(string mainImagePath, string expected)
    {
        var result = SnapshotStore.ComputeBlobDirectory(mainImagePath);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ComputeBlobDirectory_NoDirectoryComponent_ResolvesRelativeToCurrentDirectory()
    {
        var result = SnapshotStore.ComputeBlobDirectory("disk.mdr");

        Assert.Equal("disk.snapblobs", result);
    }

    [Fact]
    public void HashToBlobPath_ShardsIntoTwoCharacterSubfolderFromHashHexPrefix()
    {
        var hash = Convert.FromHexString("ab34ef0000000000000000000000000000000000000000000000000000000000");
        var directory = @"C:\disks\disk.snapblobs";

        var result = SnapshotStore.HashToBlobPath(directory, hash);

        Assert.Equal(
            Path.Combine(directory, "ab", "ab34ef0000000000000000000000000000000000000000000000000000000000.blob"),
            result);
    }

    [Fact]
    public void HashToBlobPath_IsLowercaseRegardlessOfCasingConventions()
    {
        var hash = new byte[] { 0xAB, 0xCD, 0xEF };
        var directory = @"C:\disks\disk.snapblobs";

        var result = SnapshotStore.HashToBlobPath(directory, hash);
        var fileName = Path.GetFileName(result);

        Assert.DoesNotContain(fileName, char.IsUpper);
    }

    [Fact]
    public void HashToBlobPath_DifferentHashes_ProduceDifferentPaths()
    {
        var directory = @"C:\disks\disk.snapblobs";
        var first = SnapshotStore.HashToBlobPath(directory, [0x01, 0x02, 0x03]);
        var second = SnapshotStore.HashToBlobPath(directory, [0x04, 0x05, 0x06]);

        Assert.NotEqual(first, second);
    }
}
