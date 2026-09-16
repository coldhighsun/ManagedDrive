using System.Runtime.InteropServices;

namespace ManagedDrive.Tests;

public sealed class MemoryFileSystemLowMemoryGuardTests
{
    private const int StatusInsufficientResources = unchecked((int)0xC000009A);

    [Fact]
    public void Create_BelowLowMemoryReserve_ReturnsInsufficientResources()
    {
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () => 0);

        var status = fs.Create("\\big.bin", 0, 0, (uint)FileAttributes.Normal, [], 4096,
            out _, out _, out _, out _);

        Assert.Equal(StatusInsufficientResources, status);
    }

    [Fact]
    public void Create_ZeroAllocationSize_IgnoresLowMemoryGuard()
    {
        // A brand-new file created with allocationSize 0 doesn't grow anything yet, so the guard
        // must not block it even when available memory is reported as zero.
        var fs = new MemoryFileSystem(1024 * 1024, "Label", availableMemoryProvider: () => 0);

        var status = fs.Create("\\empty.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out _, out _, out _, out _);

        Assert.Equal(0, status);
    }

    [Fact]
    public void Create_AboveLowMemoryReserve_Succeeds()
    {
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () => 1024UL * 1024 * 1024);

        var status = fs.Create("\\big.bin", 0, 0, (uint)FileAttributes.Normal, [], 4096,
            out _, out _, out _, out _);

        Assert.Equal(0, status);
    }

    [Fact]
    public void Overwrite_GrowingAllocationBelowLowMemoryReserve_ReturnsInsufficientResources()
    {
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () => 0);
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);

        var status = fs.Overwrite(fileNode!, null!, (uint)FileAttributes.Normal, false, 4096, out _);

        Assert.Equal(StatusInsufficientResources, status);
    }

    [Fact]
    public void SetFileSize_GrowingBelowLowMemoryReserve_ReturnsInsufficientResources()
    {
        var fs = new MemoryFileSystem(1024 * 1024 * 1024, "Label", availableMemoryProvider: () => 0);
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);

        var status = fs.SetFileSize(fileNode!, null!, 4096, true, out _);

        Assert.Equal(StatusInsufficientResources, status);
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
    public void Write_WithinExistingAllocationBelowLowMemoryReserve_Succeeds()
    {
        // Overwriting bytes already within the file's current allocation doesn't grow anything,
        // so the guard must not block it even when available memory is (now) reported as zero.
        var available = 1024UL * 1024 * 1024;
        var fs = new MemoryFileSystem(1024 * 1024, "Label", availableMemoryProvider: () => available);
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 4096,
            out var fileNode, out _, out _, out _);

        available = 0;

        var data = new byte[] { 1, 2, 3, 4 };
        var ptr = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
            var status = fs.Write(fileNode!, null!, ptr, 0, (uint)data.Length, false, false,
                out var bytesTransferred, out _);

            Assert.Equal(0, status);
            Assert.Equal((uint)data.Length, bytesTransferred);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
