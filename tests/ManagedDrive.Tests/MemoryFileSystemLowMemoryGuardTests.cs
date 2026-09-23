using System.Runtime.InteropServices;

namespace ManagedDrive.Tests;

public sealed class MemoryFileSystemLowMemoryGuardTests
{
    private const int StatusInsufficientResources = unchecked((int)0xC000009A);

    // The guard charges memory when backing arrays are actually allocated, not when allocation
    // size grows: growth is sparse and costs nothing until bytes are written into it.

    [Fact]
    public void Create_PreallocatingBelowLowMemoryReserve_Succeeds()
    {
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () => 0);

        var status = fs.Create("\\big.bin", 0, 0, (uint)FileAttributes.Normal, [], 64UL * 1024 * 1024,
            out _, out _, out _, out _);

        Assert.Equal(0, status);
    }

    [Fact]
    public void Overwrite_PreallocatingBelowLowMemoryReserve_Succeeds()
    {
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () => 0);
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);

        var status = fs.Overwrite(fileNode!, null!, (uint)FileAttributes.Normal, false, 64UL * 1024 * 1024, out _);

        Assert.Equal(0, status);
    }

    [Fact]
    public void SetFileSize_GrowingSparseFileBelowLowMemoryReserve_Succeeds()
    {
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () => 0);
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);

        var status = fs.SetFileSize(fileNode!, null!, 64UL * 1024 * 1024, true, out _);

        Assert.Equal(0, status);
    }

    [Fact]
    public void SetFileSize_GrowingWrittenTailBelowLowMemoryReserve_ReturnsInsufficientResources()
    {
        // The tail chunk already holds data, so growing the allocation reallocates it for real.
        // Start with just enough headroom for the first write's 512-byte chunk, so the grow below
        // can't be covered by the cached reading and must re-query.
        var available = MemoryHeadroomBudget.ReserveBytes + 512;
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () => available);
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);
        Assert.Equal(0, WriteBytes(fs, fileNode!, 0, 100, out _));
        available = 0;

        var status = fs.SetFileSize(fileNode!, null!, 4096, true, out _);

        Assert.Equal(StatusInsufficientResources, status);
    }

    [Fact]
    public void Write_IntoPreallocatedRangeBelowLowMemoryReserve_ReturnsInsufficientResources()
    {
        // Preallocating is free, but writing into the preallocated range materializes real memory,
        // so it must still be guarded - otherwise preallocate-then-write bypasses the guard.
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () => 0);
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 64UL * 1024 * 1024,
            out var fileNode, out _, out _, out _);
        Assert.Equal(0, fs.SetFileSize(fileNode!, null!, 64UL * 1024 * 1024, false, out _));

        var status = WriteBytes(fs, fileNode!, 1024 * 1024, 4096, out var bytesTransferred);

        Assert.Equal(StatusInsufficientResources, status);
        Assert.Equal(0u, bytesTransferred);
    }

    [Fact]
    public void Write_ExtendingFileDeniedByLowMemoryGuard_LeavesFileSizeUnchanged()
    {
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () => 0);
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);

        var status = WriteBytes(fs, fileNode!, 0, 4096, out _);

        Assert.Equal(StatusInsufficientResources, status);
        Assert.Equal(0UL, ((FileNode)fileNode!).FileInfo.FileSize);
    }

    [Fact]
    public void Write_ExtendingFileBelowLowMemoryReserve_ReturnsInsufficientResources()
    {
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () => 0);
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);

        var data = new byte[] { 1, 2, 3, 4 };
        var ptr = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
            var status = fs.Write(fileNode!, null!, ptr, 0, (uint)data.Length, false, false,
                out var bytesTransferred, out _);

            Assert.Equal(StatusInsufficientResources, status);
            Assert.Equal(0u, bytesTransferred);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void Write_OverAlreadyWrittenBytesBelowLowMemoryReserve_Succeeds()
    {
        // Rewriting bytes whose backing memory already exists allocates nothing, so the guard
        // must not block it even when available memory is (now) reported as zero.
        var available = 1024UL * 1024 * 1024;
        var fs = new MemoryFileSystem(1024 * 1024, "Label", availableMemoryProvider: () => available);
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);
        Assert.Equal(0, WriteBytes(fs, fileNode!, 0, 4096, out _));

        available = 0;

        var status = WriteBytes(fs, fileNode!, 0, 4096, out var bytesTransferred);

        Assert.Equal(0, status);
        Assert.Equal(4096u, bytesTransferred);
    }

    [Fact]
    public void GrowingWrites_ReuseCachedReading_InsteadOfQueryingEveryTime()
    {
        var queries = 0;
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () =>
        {
            queries++;
            return 4UL * 1024 * 1024 * 1024;
        });
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(0, AppendBytes(fs, fileNode!, 512));
        }

        // Plenty of headroom, so one reading covers every write inside the refresh window. Allow
        // a few extra in case a slow CI machine stalls past the window mid-loop; querying on every
        // write would be 100.
        Assert.InRange(queries, 1, 5);
    }

    [Fact]
    public void GrowingWrite_BeyondCachedHeadroom_RequeriesAndRecoversOnceMemoryFrees()
    {
        const ulong reserve = 256UL * 1024 * 1024;
        var available = reserve + 1024;
        var queries = 0;
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () =>
        {
            queries++;
            return available;
        });
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);

        Assert.Equal(0, AppendBytes(fs, fileNode!, 1024));
        available = reserve; // the 1 KiB just granted is no longer free

        // The cached budget is used up, so the next growth must re-query rather than trust it.
        Assert.Equal(StatusInsufficientResources, AppendBytes(fs, fileNode!, 512));
        Assert.Equal(2, queries);

        available = reserve + 1024 * 1024;

        // A denial is never sticky: the budget is still short, so this re-queries and sees the
        // memory that was freed in the meantime.
        Assert.Equal(0, AppendBytes(fs, fileNode!, 512));
        Assert.Equal(3, queries);
    }

    private static int WriteBytes(MemoryFileSystem fs, object fileNode, ulong offset, int count, out uint bytesTransferred)
    {
        var ptr = Marshal.AllocHGlobal(count);
        try
        {
            return fs.Write(fileNode, null!, ptr, offset, (uint)count, writeToEndOfFile: false,
                constrainedIo: false, out bytesTransferred, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static int AppendBytes(MemoryFileSystem fs, object fileNode, int count)
    {
        var ptr = Marshal.AllocHGlobal(count);
        try
        {
            return fs.Write(fileNode, null!, ptr, 0, (uint)count, writeToEndOfFile: true,
                constrainedIo: false, out _, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
