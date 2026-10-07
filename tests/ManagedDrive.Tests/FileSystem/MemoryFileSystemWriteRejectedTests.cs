using System.Runtime.InteropServices;

namespace ManagedDrive.Tests;

public sealed class MemoryFileSystemWriteRejectedTests
{
    private static MemoryFileSystem CreateFileSystem(ulong capacity, Func<ulong> availableMemory, out object fileNode)
    {
        var fs = new MemoryFileSystem(capacity, "Label", availableMemoryProvider: availableMemory);
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var node, out _, out _, out _);
        fileNode = node!;
        return fs;
    }

    [Fact]
    public void Write_DeniedByLowMemoryGuard_RaisesWriteRejectedLowMemory()
    {
        var fs = CreateFileSystem(1024UL * 1024 * 1024, () => 0, out var fileNode);
        var reasons = new List<WriteRejectionReason>();
        fs.WriteRejected += reasons.Add;

        WriteBytes(fs, fileNode, 0, 4096);

        Assert.Equal([WriteRejectionReason.LowMemory], reasons);
    }

    [Fact]
    public void Write_DeniedRepeatedly_RaisesOncePerInterval()
    {
        // A failed copy retries over and over; the subscriber must hear about it once.
        var fs = CreateFileSystem(1024UL * 1024 * 1024, () => 0, out var fileNode);
        var reasons = new List<WriteRejectionReason>();
        fs.WriteRejected += reasons.Add;

        for (var i = 0; i < 50; i++)
        {
            WriteBytes(fs, fileNode, 0, 4096);
        }

        Assert.Single(reasons);
    }

    [Fact]
    public void SetFileSize_GrowingWrittenTailBelowReserve_RaisesWriteRejectedLowMemory()
    {
        var available = MemoryHeadroomBudget.ReserveBytes + 512;
        var fs = CreateFileSystem(1024UL * 1024 * 1024, () => available, out var fileNode);
        Assert.Equal(0, WriteBytes(fs, fileNode, 0, 100));
        available = 0;
        var reasons = new List<WriteRejectionReason>();
        fs.WriteRejected += reasons.Add;

        fs.SetFileSize(fileNode, null!, 4096, true, out _);

        Assert.Equal([WriteRejectionReason.LowMemory], reasons);
    }

    [Fact]
    public void Write_BeyondCapacity_RaisesWriteRejectedDiskFull()
    {
        var fs = CreateFileSystem(1024 * 1024, () => ulong.MaxValue, out var fileNode);
        var reasons = new List<WriteRejectionReason>();
        fs.WriteRejected += reasons.Add;

        WriteBytes(fs, fileNode, 2 * 1024 * 1024, 4096);

        Assert.Equal([WriteRejectionReason.DiskFull], reasons);
    }

    [Fact]
    public void Write_FillingTheVolumeAcrossFiles_RaisesWriteRejectedDiskFull()
    {
        var fs = CreateFileSystem(64 * 1024, () => ulong.MaxValue, out var fileNode);
        fs.Create("\\other.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var otherNode, out _, out _, out _);
        Assert.Equal(0, WriteBytes(fs, otherNode!, 0, 60 * 1024));
        var reasons = new List<WriteRejectionReason>();
        fs.WriteRejected += reasons.Add;

        WriteBytes(fs, fileNode, 0, 32 * 1024);

        Assert.Equal([WriteRejectionReason.DiskFull], reasons);
    }

    [Fact]
    public void Write_RejectionsOfDifferentReasons_AreLimitedIndependently()
    {
        var available = 0UL;
        var fs = CreateFileSystem(1024 * 1024, () => available, out var fileNode);
        var reasons = new List<WriteRejectionReason>();
        fs.WriteRejected += reasons.Add;

        WriteBytes(fs, fileNode, 0, 4096);
        WriteBytes(fs, fileNode, 2 * 1024 * 1024, 4096);
        WriteBytes(fs, fileNode, 0, 4096);
        WriteBytes(fs, fileNode, 2 * 1024 * 1024, 4096);

        Assert.Equal([WriteRejectionReason.LowMemory, WriteRejectionReason.DiskFull], reasons);
    }

    [Fact]
    public void Write_Succeeding_DoesNotRaiseWriteRejected()
    {
        var fs = CreateFileSystem(1024 * 1024, () => ulong.MaxValue, out var fileNode);
        var reasons = new List<WriteRejectionReason>();
        fs.WriteRejected += reasons.Add;

        var status = WriteBytes(fs, fileNode, 0, 4096);

        Assert.Equal(0, status);
        Assert.Empty(reasons);
    }

    [Fact]
    public void Write_DeniedWithoutSubscriber_StillReturnsTheStatus()
    {
        var fs = CreateFileSystem(1024UL * 1024 * 1024, () => 0, out var fileNode);

        var status = WriteBytes(fs, fileNode, 0, 4096);

        Assert.Equal(unchecked((int)0xC000009A), status);
    }

    private static int WriteBytes(MemoryFileSystem fs, object fileNode, ulong offset, int count)
    {
        var ptr = Marshal.AllocHGlobal(count);
        try
        {
            return fs.Write(fileNode, null!, ptr, offset, (uint)count, writeToEndOfFile: false,
                constrainedIo: false, out _, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
